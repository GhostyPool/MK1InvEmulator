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
                    Debug.exitWithError("ERROR: Game certificate collection could not be found! Try re-running the application!");
                }

                try
                {
                    File.AppendAllText(gameCertCol, "\n1337\n=========================================\n" + cert);
                }
                catch (Exception ex)
                {
                    Debug.printError("ERROR: Could not write to game certificate collection file! Please restart and try again.");
                    Debug.exitWithError("Full error: " + ex);
                }

                //Move original pak file
                File.Move(certPakFilePath, certPakFilePath + ".original");

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
                Debug.exitWithError("Patching unsuccessful during unpacking! Make sure you are providing the righ AES key!");
            }
        }

        private static string? getbase64Certificate()
        {
            var cert = Certificate.UseDefault().GetX509Certificate();

            if (cert == null)
            {
                Debug.printError("ERROR: Could not retrieve fluxzy certificate!");
                return null;
            }

            if (!isCertInstalled(cert))
            {
                Debug.printError("ERROR: Certificate not installed! Please press 'Yes' on the certificate pop-up when launching the application!");
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
        public static Process? startClash(int port)
        {
            string configPath = @"bin\clash\config.yaml";
            string exePath = @"bin\clash\clash-win64.exe";

            if (File.Exists(exePath) && File.Exists(configPath))
            {
                var configStream = loadYaml(configPath);
                var config = (YamlMappingNode)configStream.Documents[0].RootNode;

                //Add port from config
                config.Children[new YamlScalarNode("mixed-port")] = new YamlScalarNode(port.ToString());

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
                Debug.printError("ERROR: Clash dependency is missing! Please re-download the program and make sure to keep all files!");
                return null;
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
                Debug.printError("ERROR: Repak dependency is missing! Please re-download the program and make sure to keep all files!");
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
                Debug.printError("ERROR: Repak dependency is missing! Please re-download the program and make sure to keep all files!");
                return null;
            }
        }
    }

    internal class InvHelpers
    {
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
                if (!Directory.Exists("debug")) Directory.CreateDirectory("debug");

                await File.WriteAllBytesAsync(Path.Combine("debug", debugFileName), bytes);
            }
        }

        public static void printError(string error)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(error);
            Console.ResetColor();
        }

        public static void exitWithError(string error)
        {
            printError(error);
            Console.ReadLine();
            Environment.Exit(1);
        }

        public static void readLineAndExit()
        {
            Console.ReadLine();
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
