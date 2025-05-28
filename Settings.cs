using IniParser;
using IniParser.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Emulator
{
    internal class Settings
    {
        private FileIniDataParser parser = new FileIniDataParser();
        private IniData settingsData;

        public FileIniDataParser Parser => parser;

        public IniData SettingsData => settingsData;

        public Settings()
        {
            try
            {
                settingsData = parser.ReadFile("settings.ini");
            }
            catch (Exception)
            {
                settingsData = new IniData();

                //Defaults
                settingsData["General"]["DebugLogging"] = "false";
                settingsData["General"]["FirstBoot"] = "true";
                settingsData["General"]["FluxzyPort"] = "8888";
                settingsData["General"]["ClashPort"] = "7890";

                parser.WriteFile("settings.ini", settingsData);
            }

            Debug.setDebugLogging(settingsData["General"]["DebugLogging"]);
        }

    }
}
