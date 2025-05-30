using Fluxzy.Certificates;
using HydraDotNet.Core.Encoding;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Emulator
{
    internal class HydraHelpers
    {
        public static Dictionary<object, object?> decodeFromHydra(byte[] byteArray)
        {
            using var decoder = new HydraDecoder(byteArray);
            var rawObject = decoder.ReadValue();

            return rawObject as Dictionary<object, object?>;
        }

        public static async Task<byte[]> encodeFromDictionary(Dictionary<object, object?> dictionary)
        {
            await using var encoder = new HydraEncoder();

            encoder.WriteValue(dictionary);

            return await encoder.GetBufferAsync();
        }
    }

    internal class FirstBootHelpers
    {
        public static string? getMk1SteamFolder()
        {
            using var mk1SteamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? steamPath = mk1SteamKey?.GetValue("SteamPath").ToString();

            if (steamPath == null) return null;

            steamPath = steamPath.Replace("/", "\\");
            Debug.debugLog("Detected steam path: " + steamPath);

            string libraryVdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            Debug.debugLog("Detected libraryfolders.vdf path: " + libraryVdfPath);

            if (!File.Exists(libraryVdfPath)) return null;

            //Find game and return path
            string libraryPath = "";
            foreach (var line in File.ReadAllLines(libraryVdfPath))
            {
                if (line.Trim().ToLower().Contains("path"))
                {
                    libraryPath = line.Trim().Replace("\"path\"", "").Trim().Replace("\\\\", "\\");
                    Debug.debugLog("Detected path: " + libraryPath);
                }

                if (line.Trim().Contains("\"1971870\""))
                {
                    Debug.debugLog("Found MK1 in library: " + libraryPath);

                    string pakFolderPath = Path.Combine(libraryPath.Replace("\"", ""), "steamapps", "common", "Mortal Kombat 1", "MK12", "Content", "Paks");
                    Debug.debugLog("Found MK1 Pak path at: " + pakFolderPath);

                    if (Directory.Exists(pakFolderPath) && File.Exists(Path.Combine(pakFolderPath, "pakpc2-WindowsNoEditor.pak"))) return pakFolderPath;
                }
            }

            return null;
        }

        public static void printFirstBootMessage(string msg)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine(msg);
            Console.ResetColor();
        }

        public static string? providePathManually()
        {
            printFirstBootMessage("Path could not be automatically found/is invalid. Please provide the path to your game's Paks folder on the next line.");

            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write("Pak folder path: ");
            Console.ResetColor();

            string? inputPath = Console.ReadLine();
            string? pakFolderPath = !string.IsNullOrEmpty(inputPath) ? inputPath.Replace("/", "\\") : null;

            if (!string.IsNullOrEmpty(pakFolderPath) && File.Exists(Path.Combine(pakFolderPath, "pakpc2-WindowsNoEditor.pak"))) return pakFolderPath;

            return null;
        }

        public static string? provideAES()
        {
            printFirstBootMessage("Please enter MK1's AES key (this can be found in the Nexus description): ");

            string? aesInput = Console.ReadLine();

            return !string.IsNullOrEmpty(aesInput) ? aesInput : null;
        }

        public static void patchGame(string inputPath, string AES)
        {
            string certPakFilePath = Path.Combine(inputPath, "pakpc2-WindowsNoEditor.pak");
            string tempFolder = Path.Combine(inputPath, "temp_patching");

            //Unpack game certificate
            var repakUnpack = DependencyHelpers.repakUnpack(certPakFilePath, tempFolder, AES);

            if (repakUnpack == null) Debug.readLineAndExit();

            repakUnpack.WaitForExit();

            if (repakUnpack.ExitCode == 0)
            {
                string? cert = getbase64Certificate();

                if (cert == null) Debug.readLineAndExit();

                //Modify game certificate collection
                var gameCertCol = Path.Combine(tempFolder, "MK12", "Content", "Certificates", "Windows", "cacert.pem");

                if (!File.Exists(gameCertCol))
                {
                    Debug.exitWithError("Game certificate collection could not be found! Try re-running the application!");
                }

                try
                {
                    File.AppendAllText(gameCertCol, "\n1337\n=========================================\n" + cert);
                }
                catch (Exception ex)
                {
                    Debug.printError("Could not write to game certificate collection file! Please restart and try again.");
                    Debug.exitWithError("Full error: " + ex);
                }

                //Move original pak file and account for already patched files
                string originalPakBackup = certPakFilePath + ".original";
                if (!File.Exists(originalPakBackup))
                {
                    File.Move(certPakFilePath, originalPakBackup);
                }
                else
                {
                    if (File.Exists(certPakFilePath))
                    {
                        Debug.debugLog("Detected already patched pak file, deleting...");
                        //Delete old patched file
                        File.Delete(certPakFilePath);
                    }
                }

                //Repack everything back
                var repakPack = DependencyHelpers.repakRepack(tempFolder, certPakFilePath, "Zlib");

                if (repakPack == null) Debug.readLineAndExit();

                repakPack.WaitForExit();

                if (repakPack.ExitCode == 0)
                {
                    //Cleanup
                    Directory.Delete(tempFolder, true);
                }
                else
                {
                    Debug.exitWithError("Patching unsuccessful during packing!");
                }
            }
            else
            {
                Debug.printError("Patching unsuccessful during unpacking! Make sure you are providing the righ AES key!");
                Debug.exitWithError("If your game is inside a protected folder (like Program Files), try running as admin!");
            }
        }

        private static string? getbase64Certificate()
        {
            var cert = Certificate.UseDefault().GetX509Certificate();

            if (cert == null)
            {
                Debug.printError("Could not retrieve fluxzy certificate!");
                return null;
            }

            if (!isCertInstalled(cert))
            {
                Debug.printError("Certificate not installed! Please press 'Yes' on the certificate pop-up when launching the application!");
                return null;
            }

            byte[] certDER = cert.Export(X509ContentType.Cert);

            string base64cert = "-----BEGIN CERTIFICATE-----\n" + 
                Convert.ToBase64String(certDER, Base64FormattingOptions.InsertLineBreaks) +
                                "\n-----END CERTIFICATE-----";

            Debug.debugLog("Base64 cert: " + base64cert);

            return base64cert;
        }

        private static bool isCertInstalled(X509Certificate2 cert)
        {
            using var certStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            certStore.Open(OpenFlags.ReadOnly); 

            foreach (var storeCert in certStore.Certificates)
            {
                if (storeCert.Thumbprint.Equals(cert.Thumbprint))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal static class DependencyHelpers
    {
        public static Process? startClash(int clashPort, int fluxzyPort)
        {
            string configPath = @"bin\clash\config.yaml";
            string exePath = @"bin\clash\clash-win64.exe";

            if (File.Exists(exePath) && File.Exists(configPath))
            {
                var configStream = loadYaml(configPath);
                var config = (YamlMappingNode)configStream.Documents[0].RootNode;

                //Add ports from config
                config.Children[new YamlScalarNode("mixed-port")] = new YamlScalarNode(clashPort.ToString());

                addFluxzyProxyToClashConfig(config, fluxzyPort);

                using (var writer = new StreamWriter(configPath))
                {
                    configStream.Save(writer, false);
                }

                var clashProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "-f config.yaml",
                        WorkingDirectory = "bin\\clash",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                clashProcess.Start();
                return clashProcess;
            }
            else
            {
                Debug.printError("Clash dependency is missing! Please re-download the program and make sure to keep all files!");
                return null;
            }
        }

        private static void addFluxzyProxyToClashConfig(YamlMappingNode config, int fluxzyPort)
        {
            var proxiesNode = (YamlSequenceNode)config.Children[new YamlScalarNode("proxies")];

            foreach (YamlMappingNode entries in proxiesNode)
            {
                var name = (YamlScalarNode)entries.Children[new YamlScalarNode("name")];

                if (name.Value == "fluxzy")
                {
                    entries.Children[new YamlScalarNode("port")] = new YamlScalarNode(fluxzyPort.ToString());
                    break;
                }
            }
        }

        private static YamlStream loadYaml(string file)
        {
            using var reader = new StreamReader(file);
            var yaml = new YamlStream();
            yaml.Load(reader);

            return yaml;
        }

        public static Process? repakUnpack(string inputPath, string outputPath, string AES)
        {
            string repakExe = @"bin\repak\repak.exe";

            if (File.Exists(repakExe))
            {
                var repak = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = repakExe,
                        Arguments = String.Format("-a \"{0}\" unpack \"{1}\" -o \"{2}\"", AES, inputPath, outputPath),
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                
                repak.Start();
                return repak;
            }
            else
            {
                Debug.printError("Repak dependency is missing! Please re-download the program and make sure to keep all files!");
                return null;
            }
        }

        public static Process? repakRepack(string inputPath, string outputPath, string compression)
        {
            string repakExe = @"bin\repak\repak.exe";

            if (File.Exists(repakExe))
            {
                var repak = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = repakExe,
                        Arguments = String.Format("pack --compression \"{0}\" \"{1}\" \"{2}\"", compression, inputPath, outputPath),
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                
                repak.Start();
                return repak;
            }
            else
            {
                Debug.printError("Repak dependency is missing! Please re-download the program and make sure to keep all files!");
                return null;
            }
        }
    }

    internal class InvHelpers
    {
        public static bool hasEquippedItems(Dictionary<object, object?> itemDict)
        {
            if (((Dictionary<object, object?>)((Dictionary<object, object?>)((Dictionary<object, object?>)itemDict["data"])["slots"])["slots"]).Count > 0)
            {
                return true;
            }

            return false;
        }

        public static Dictionary<object, object?> getEquippedItemsDict(Dictionary<object, object?> itemDict)
        {
            return (Dictionary<object, object?>)((Dictionary<object, object?>)((Dictionary<object, object?>)itemDict["data"])["slots"])["slots"];
        }

        public static bool getIsFavouriteValue(Dictionary<object, object?> itemDict)
        {
            return (bool)((Dictionary<object, object?>)itemDict["data"])["bIsFavorite"];
        }

        public static Dictionary<object, object?>? findItemById(string id, Object[] itemsArray)
        {

            Debug.debugLog("Item ID to look for: " + id);

            for (int i = 0; i < itemsArray.Length; i++)
            {
                if (itemsArray[i] is Dictionary<object, object?> itemDict
                    && itemDict.TryGetValue("id", out var itemId)
                    && itemId is string stringId)
                {

                    if (stringId.Equals(id))
                    {
                        Debug.debugLog("Found item with id: " + id);
                        return itemDict;
                    }
                }
            }

            return null;
        }

        public static Dictionary<object, object?>? findItemBySlugName(string slugName, Object[] itemsArray)
        {
            for (int i = 0; i < itemsArray.Length; i++)
            {
                if (itemsArray[i] is Dictionary<object, object?> itemDict
                    && itemDict.TryGetValue("item_slug", out object itemSlug)
                    && itemSlug is string slugString)
                {
                    if (slugString.Equals(slugName))
                    {
                        Debug.debugLog("Found item by slug: " + slugString);
                        return itemDict;
                    }
                }
            }

            return null;
        }

        public static string? getItemType(Dictionary<object, object?> itemDict)
        {
            if (((string)itemDict["item_slug"]).Contains("Gear"))
            {
                return "Gear";
            }
            else if (((string)itemDict["item_slug"]).Contains("Skin"))
            {
                return "Skin";
            }
            else if (((string)itemDict["item_slug"]).Contains("SeasonalFatality"))
            {
                return "SeasonalFatality";
            }

            return null;
        }

        public static (byte lvl, ushort? exp16, uint? exp32, Dictionary<object, object?> data)? getPlayerLvlAndExp(Dictionary<object, object?> inv)
        {
            var current_items = (Object[])((Dictionary<object, object?>)((Dictionary<object, object?>)inv["body"])["response"])["current_items"];

            var profileDict = findItemBySlugName("Profile", current_items);

            if (profileDict != null)
            {
                var data = (Dictionary<object, object?>)profileDict["data"];

                byte lvl = (byte)((Dictionary<object, object?>)data["currentLevel"])["val"];

                if (((Dictionary<object, object?>)data["experience"])["val"] is ushort exp16)
                {
                    Debug.debugLog(String.Format("Found player level: {0} and experience of type UInt16: {1}", lvl, exp16));

                    return (lvl, exp16, null, data);
                }
                else if (((Dictionary<object, object?>)data["experience"])["val"] is uint exp32)
                {
                    Debug.debugLog(String.Format("Found player level: {0} and experience of type UInt32: {1}", lvl, exp32));

                    return (lvl, null, exp32, data);
                }
                else
                {
                    Debug.printError("Could not determine type of player experience!");
                }
            }
            else
            {
                Debug.printError("Could not find player profile!");
            }

            return null;
        }

        public static void checkForUpdate(string invFolder, string invFileName, string appdataFolder)
        {
            bool newUser = false;
            string updateFolder = "update";

            if (!isInAppdata(invFolder, invFileName, appdataFolder))
            {
                string invFilePath = Path.Combine(invFolder, invFileName);
                string updateInvFilePath = Path.Combine(updateFolder, invFileName);

                if (File.Exists(invFilePath))
                {
                    moveToAppdata(invFilePath, Path.Combine(appdataFolder, invFilePath));

                    string bakInv = Path.Combine(invFolder, "inventory.bin.bak");
                    if (File.Exists(bakInv)) moveToAppdata(bakInv, Path.Combine(appdataFolder, bakInv));

                    try
                    {
                        //Delete local inv folder
                        Directory.Delete(invFolder);
                    }
                    catch (Exception e)
                    {
                        Debug.printWarning("Could not remove directory: " + invFolder);
                        Debug.printWarning("Full exception: " + e);
                    }
                }
                else if (File.Exists(updateInvFilePath))
                {
                    moveToAppdata(updateInvFilePath, Path.Combine(appdataFolder, invFilePath));
                    newUser = true;
                }
                else
                {
                    Debug.exitWithError("Required file 'inventory.bin' is missing! Please re-download the program and make sure to keep all files!");
                }
            }

            if (Directory.Exists(updateFolder))
            {
                if (!newUser)
                {
                    bool? invUpdate = checkUpdateInfo(updateFolder);

                    if (!invUpdate.HasValue) return;

                    if (invUpdate.Value)
                    {
                        Debug.printCaution("There is an update for your inventory!");

                        var newInvFilePath = Path.Combine(updateFolder, invFileName);
                        if (!File.Exists(newInvFilePath))
                        {
                            Debug.printWarning("Could not find new inventory file, could not perform update!");
                            Directory.Delete(updateFolder, true);
                            return;
                        }

                        var newInv = HydraHelpers.decodeFromHydra(File.ReadAllBytes(newInvFilePath));
                        var oldInv = HydraHelpers.decodeFromHydra(File.ReadAllBytes(Path.Combine(appdataFolder, invFolder, invFileName)));


                        InventoryContainer.updateInventoryToNewVersion(oldInv, newInv);

                        Debug.printSuccess("Successfully updated inventory!");
                    }
                }

                //Delete update folder
                Debug.debugLog("Deleting update directory...");
                Directory.Delete(updateFolder, true);
            }
        }

        public static bool isInAppdata(string fileFolder, string fileName, string appdataFolder)
        {
            Debug.debugLog("Checking if inv is in appdata...");
            if (!File.Exists(Path.Combine(appdataFolder, fileFolder, fileName)))
            {
                //Create inv directory
                Directory.CreateDirectory(Path.Combine(appdataFolder, fileFolder));

                return false;
            }

            Debug.debugLog("Found file in appdata folder: " + (appdataFolder + fileFolder));
            return true;
        }

        public static void moveToAppdata(string inputFilePath, string outputFilePath)
        {
            if (!File.Exists(outputFilePath)
                && File.Exists(inputFilePath))
            {
                Debug.debugLog(String.Format("Moving file: {0} into appdata....", inputFilePath));
                File.Move(inputFilePath, outputFilePath);

                Debug.debugLog(String.Format("Successfully moved: {0} into appdata!", inputFilePath));
            }
        }

        public static bool? checkUpdateInfo(string updateFolder)
        {
            string updateFileName = "info.txt";
            string updateFilePath = Path.Combine(updateFolder, updateFileName);

            bool invUpdate = false;

            if (File.Exists(updateFilePath))
            {
                foreach (var line in File.ReadAllLines(updateFilePath))
                {
                    if (line.StartsWith("invUpdate"))
                    {
                        invUpdate = bool.TryParse(line.Replace("invUpdate=", ""), out bool validValue) ? validValue : false;
                        Debug.debugLog("Value of 'invUpdate' bool: " + invUpdate);
                    }
                }

                return invUpdate;
            }
            else
            {
                Debug.printWarning("Could not read update file 'info.txt'! Your inventory may not get updated!");
                return null;
            }
        }

        public static string generateRandomId()
        {
            byte[] bytes = new byte[12];

            using (var ran = RandomNumberGenerator.Create())
            {
                ran.GetBytes(bytes);
            }

            return BitConverter.ToString(bytes).Replace("-", "").ToLower();
        }
    }

    internal class Debug
    {
        private static bool debugLogging = false;

        public static void debugLog(string msg)
        {
            if (debugLogging)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("DEBUG: " + msg);
                Console.ResetColor();
            }
        }

        public static void setDebugLogging(string dbgLoggingSetting)
        {
            debugLogging = bool.TryParse(dbgLoggingSetting, out bool setting) ? setting : false;
        }

        public static bool isDebugLogging()
        {
            return debugLogging;
        }

        public static async Task writeDebugFile(string debugFileName, byte[] bytes)
        {
            if (isDebugLogging())
            {
                string debugFolder = Path.Combine(InventoryContainer.AppdataFolder, "debug");
                if (!Directory.Exists(debugFolder)) Directory.CreateDirectory(debugFolder);

                await File.WriteAllBytesAsync(Path.Combine(debugFolder, debugFileName), bytes);
            }
        }

        public static void printError(string error)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("ERROR: " + error);
            Console.ResetColor();
        }

        public static void printWarning(string warning)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("WARNING: " + warning);
            Console.ResetColor();
        }

        public static void printCaution(string caution)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("CAUTION: " + caution);
            Console.ResetColor();
        }

        public static void exitWithError(string error)
        {
            printError(error);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("Press any key to exit");
            Console.ResetColor();
            Console.ReadLine();
            Environment.Exit(1);
        }

        public static void readLineAndExit()
        {
            Console.ReadLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("Press any key to exit");
            Console.ResetColor();
            Environment.Exit(1);
        }

        public static void printSuccess(string successMsg)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(successMsg);
            Console.ResetColor();
        }
    }
}
