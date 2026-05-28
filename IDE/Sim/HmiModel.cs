using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OSDevIDE.Sim
{
    /// <summary>
    /// Serializable widget descriptor — everything needed to recreate a widget
    /// on the canvas, including its position, size, tag binding, and any
    /// widget-specific props (min/max, colors, label, etc.).
    ///
    /// On-disk format is JSON next to the C source (`<source>.hmi`):
    ///   {
    ///     "name": "Boiler HMI",
    ///     "width": 1100, "height": 600,
    ///     "widgets": [
    ///       { "type": "Tank",   "x": 100, "y": 50, "w": 80, "h": 200, "tag": "B1_Level", "min": 0, "max": 100, "label": "Boiler 1" },
    ///       { "type": "Flame",  "x": 110, "y": 260, "w": 60, "h": 80, "tag": "B1_Run" },
    ///       ...
    ///     ]
    ///   }
    ///
    /// The model stays plain-data; runtime visuals are built by
    /// <see cref="ThemedWidgets"/>.
    /// </summary>
    public class HmiWidgetModel
    {
        public string Type { get; set; } = "Lamp";
        public double X { get; set; } = 50;
        public double Y { get; set; } = 50;
        public double W { get; set; } = 80;
        public double H { get; set; } = 80;
        public string? Tag { get; set; }
        public string? Label { get; set; }
        public double Min { get; set; } = 0;
        public double Max { get; set; } = 100;
        public string? Color { get; set; }       // optional theme override (#RRGGBB)
        public string? Format { get; set; }      // for NumberDisplay (e.g., "F2", "F0")
        public string? Mode { get; set; }        // for Button: "Momentary" | "Toggle" | "Latch"
        public int Samples { get; set; } = 120;  // for Trend
        public int Z { get; set; } = 0;          // stacking order on canvas
        public double LowAlarm  { get; set; }    // alarm bands for analog widgets
        public double HighAlarm { get; set; }
        public string? Units { get; set; }       // engineering units suffix ("PSI", "°F", "GPM")
    }

    public class HmiDoc
    {
        public string Name { get; set; } = "HMI";
        public double Width { get; set; } = 1100;
        public double Height { get; set; } = 600;
        public List<HmiWidgetModel> Widgets { get; set; } = new();

        private static JsonSerializerOptions Opts() => new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        public static HmiDoc Load(string path)
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<HmiDoc>(text, Opts()) ?? new HmiDoc();
        }

        public void Save(string path)
        {
            var text = JsonSerializer.Serialize(this, Opts());
            File.WriteAllText(path, text);
        }

        /// <summary>
        /// Find the conventional .hmi file for a project. Looks for
        /// `<projectPath>/Source/*.hmi`, returns the first match or
        /// `Source/main.hmi` (which may not exist yet) as a default.
        /// </summary>
        public static string ConventionalPath(string projectPath)
        {
            var sourceDir = Path.Combine(projectPath, "Source");
            if (Directory.Exists(sourceDir))
            {
                var hmis = Directory.GetFiles(sourceDir, "*.hmi");
                if (hmis.Length > 0) return hmis[0];
            }
            return Path.Combine(sourceDir, "main.hmi");
        }
    }
}
