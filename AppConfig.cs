using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace D2RCameraTool
{
    public class AppConfig
    {
        private readonly string filePath;

        public AppConfig(string filePath) {
            this.filePath = filePath;
        }

        public Dictionary<string, string> Load() {
            Dictionary<string, string> data = new Dictionary<string, string>();

            // Missing file is normal on first run: fall back to built-in defaults rather
            // than blocking startup with a modal dialog. The original called
            // MessageBox.Show here, and because the path was relative and settings.ini was
            // not shipped next to the executable, a fresh install could not start at all.
            if (!File.Exists(filePath)) {
                return data;
            }

            string[] lines = File.ReadAllLines(filePath);

            foreach (string line in lines) {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith(";"))
                    continue;

                string[] parts = line.Split('=');
                if (parts.Length == 2) {
                    string key = parts[0].Trim();
                    string value = parts[1].Trim();
                    data[key] = value;
                }
            }

            return data;
        }

        public void Save(Dictionary<string, string> data) {
            try {
                using (StreamWriter writer = new StreamWriter(filePath)) {
                    foreach (var kvp in data) {
                        writer.WriteLine($"{kvp.Key}={kvp.Value}");
                    }
                }
            } catch (Exception ex) {
                // A read-only install directory must not take the app down on exit.
                System.Diagnostics.Debug.WriteLine("could not save settings: " + ex.Message);
            }
        }
    }
}