using Fluxzy.Utils.Curl;
using HydraDotNet.Core.Extensions;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Security.Claims;
using YamlDotNet.RepresentationModel;
using static Emulator.FirstBootHelpers;

namespace Emulator
{
    internal class Program
    {
        private static Process? clashProcess;
        private static PacketHandling? proxy;
        private static bool hasShutDown = false;

        static async Task Main(string[] args)
        {
            //Handle closing app
            AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;

            Console.CancelKeyPress += Console_CancelKeyPress;

            //Starting
            Console.WriteLine("Starting up...");

            Console.WriteLine("Loading settings...");

            Settings settings = new Settings();

            //Randomize things
            InventoryContainer.randomizeAccountId();

            //Start clash
            int clashPort = int.TryParse(settings.SettingsData["General"]["ClashPort"], out int validclashport) ? validclashport : 7890;

            Console.WriteLine("Starting clash...");
            clashProcess = DependencyHelpers.startClash(clashPort);

            if (clashProcess == null) Debug.readLineAndExit();

            Debug.printSuccess(String.Format("Clash successfully started on address 127.0.0.1:{0}", clashPort));

            //Start fluxzy proxy
            Console.WriteLine("Starting fluxzy proxy...");

            //Parse some settings
            int fluxzyPort = int.TryParse(settings.SettingsData["General"]["FluxzyPort"], out int validfluxzyport) ? validfluxzyport : 8888;
            bool firstBoot = bool.TryParse(settings.SettingsData["General"]["FirstBoot"], out bool firstBootResult) ? firstBootResult : true;

            if (firstBoot)
            {
                Console.Clear();
                printFirstBootMessage("You will be prompted to accept a certificate. (This is necessary for the application to work)");
                printFirstBootMessage("Please press 'Yes' on the certificate");
            }

            proxy = new PacketHandling(fluxzyPort);
            await proxy.startProxy(new IPEndPoint(IPAddress.Loopback, clashPort));

            if (firstBoot)
            {
                handleFirstBoot(settings);
            }

            Debug.printSuccess(String.Format("Fluxzy proxy successfully started on address 127.0.0.1:{0}", fluxzyPort));

            Debug.printSuccess("Configured clash as system-wide proxy.");

            //Keep alive
            await Task.Delay(-1);
        }

        private static void CurrentDomain_ProcessExit(object? sender, EventArgs e)
        {
            Task.Run(shutDown).Wait();
        }

        private static void Console_CancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Shutting down...");
            Console.ResetColor();

            Task.Run(shutDown).Wait();

            Environment.Exit(0);
        }

        private static async Task shutDown()
        {
            if (hasShutDown) return;
            hasShutDown = true;

            //Shutdown fluxzy proxy
            if (proxy != null && proxy.Proxy != null)
            {
                await proxy.Proxy.DisposeAsync();
            }

            //Shutdown clash process
            if (clashProcess != null && !clashProcess.HasExited)
            {
                clashProcess.Kill();
                clashProcess.WaitForExit();

                clashProcess?.Close();
                clashProcess?.Dispose();
            }
        }

        private static void handleFirstBoot(Settings settings)
        {
            Console.Clear();
            printFirstBootMessage("Your game must be patched in order for this application to work.");

            //Try to auto-find game
            string? pakFolderPath = getMk1SteamFolder();

            if (pakFolderPath != null)
            {
                printFirstBootMessage(String.Format("MK1 Pak folder path found at: {0} ", pakFolderPath));
            }
            else
            {
                while (pakFolderPath == null)
                {
                    pakFolderPath = providePathManually();
                }
            }

            //Gather aes
            string? aes = null;

            while (aes == null)
            {
                aes = provideAES();
            }

            printFirstBootMessage("Beginning unpacking... (Do not close the program during this process)");

            patchGame(pakFolderPath, aes);

            printFirstBootMessage("Successful patching!");

            //Disable first boot
            settings.SettingsData["General"]["FirstBoot"] = "false";

            try
            {
                settings.Parser.WriteFile("settings.ini", settings.SettingsData);
            }
            catch (Exception e)
            {
                Debug.printError("ERROR: Could not write to settings.ini file!");
                Debug.printError("Full error: " + e);
            }
        }
    }
}
