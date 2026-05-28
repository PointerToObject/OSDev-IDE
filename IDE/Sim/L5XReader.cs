using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace OSDevIDE.Sim
{
    public class RungDef
    {
        public int Number;
        public string Text = "";
        public string? Comment;
        public List<RungItem> Parsed = new();
    }

    public class RoutineDef
    {
        public string Name = "";
        public List<RungDef> Rungs = new();
    }

    public class TagDef
    {
        public string Name = "";
        public string DataType = "DINT";
        public int ArraySize;
        public string? Initial;
    }

    public class PlcProgram
    {
        public string Name = "Program";
        public string MainRoutine = "MainRoutine";
        public List<TagDef> Tags = new();
        public Dictionary<string, RoutineDef> Routines = new(StringComparer.OrdinalIgnoreCase);
    }

    public static class L5XReader
    {
        public static PlcProgram Load(string path)
        {
            var prog = new PlcProgram();
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root == null) throw new InvalidDataException("L5X has no root");

            var program = root.Descendants("Program").FirstOrDefault(e =>
                (string?)e.Attribute("Use") == "Target" || e.Attribute("Use") == null);
            if (program == null) throw new InvalidDataException("L5X has no Program");

            prog.Name = (string?)program.Attribute("Name") ?? "Program";
            prog.MainRoutine = (string?)program.Attribute("MainRoutineName") ?? "MainRoutine";

            var tagsEl = program.Element("Tags");
            if (tagsEl != null)
            {
                foreach (var t in tagsEl.Elements("Tag"))
                {
                    var td = new TagDef
                    {
                        Name = (string?)t.Attribute("Name") ?? "",
                        DataType = (string?)t.Attribute("DataType") ?? "DINT",
                    };
                    var dim = (string?)t.Attribute("Dimensions");
                    if (!string.IsNullOrEmpty(dim) && int.TryParse(dim.Trim(), out var n))
                        td.ArraySize = n;
                    var dv = t.Descendants("DataValue").FirstOrDefault();
                    if (dv != null) td.Initial = (string?)dv.Attribute("Value");
                    prog.Tags.Add(td);
                }
            }

            var routinesEl = program.Element("Routines");
            if (routinesEl != null)
            {
                foreach (var r in routinesEl.Elements("Routine"))
                {
                    var rd = new RoutineDef { Name = (string?)r.Attribute("Name") ?? "" };
                    var content = r.Element("RLLContent");
                    if (content != null)
                    {
                        foreach (var rung in content.Elements("Rung"))
                        {
                            var rg = new RungDef();
                            int.TryParse((string?)rung.Attribute("Number") ?? "0", out rg.Number);
                            rg.Text = (string?)rung.Element("Text") ?? "";
                            rg.Comment = (string?)rung.Element("Comment");
                            rg.Parsed = RungParser.Parse(rg.Text);
                            rd.Rungs.Add(rg);
                        }
                    }
                    prog.Routines[rd.Name] = rd;
                }
            }

            return prog;
        }

        /// <summary>
        /// Populate a tag database from the L5X tag definitions, applying
        /// initial values where present.
        /// </summary>
        public static void HydrateDatabase(PlcProgram prog, TagDatabase db)
        {
            foreach (var td in prog.Tags)
            {
                var t = db.Add(new Tag(td.Name, td.DataType, td.ArraySize));
                if (td.Initial != null)
                {
                    if (t.IsBool) t.Bool = td.Initial.Trim() != "0";
                    else if (t.IsReal && double.TryParse(td.Initial, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var d))
                        t.Real = d;
                    else if (int.TryParse(td.Initial, out var i))
                        t.Int = i;
                }
            }
        }
    }
}
