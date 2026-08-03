using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CadToolsApp.Models;
using netDxf;

namespace CadToolsApp.Services
{
    // netDxf has no support for the MULTILEADER entity (confirmed against every published
    // netDxf release) — it silently drops these entities on load. Real-world cable drawings
    // store their type/method callout annotations exclusively as MULTILEADER, so this reads
    // them directly from the raw DXF group codes instead of going through netDxf.
    //
    // Relevant structure (whitespace-stripped group code / value pairs):
    //   0  MULTILEADER
    //   8  <layer>
    //   300  CONTEXT_DATA{
    //     304  "<annotation text>"        <- the real callout text
    //     302  LEADER{
    //       304  LEADER_LINE{
    //         10/20/30  <point>            <- leader arrowhead, touches the annotated geometry
    //       305  }
    //     301  }
    //   303  }
    // A MULTILEADER can contain multiple LEADER{...} branches (multiple arrowheads for one
    // annotation). Any group-code value ending in "{" opens a nested block; a value of "}"
    // closes whatever block is currently open — closing codes vary (301/303/305) but always
    // just pop the innermost open block.
    public static class MultiLeaderReader
    {
        public static List<CalloutAnnotation> ReadCallouts(string dxfPath)
        {
            var result = new List<CalloutAnnotation>();

            string[] lines;
            try
            {
                // DXF text is ANSI (Windows-1252 per $DWGCODEPAGE); Latin1 decodes every byte
                // without throwing and agrees with 1252 outside the rarely-used 0x80-0x9F range,
                // without pulling in the System.Text.Encoding.CodePages package for one file read.
                using var stream = new FileStream(dxfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.Latin1);
                string text = reader.ReadToEnd();
                lines = text.Replace("\r\n", "\n").Split('\n');
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to read '{dxfPath}' for callout parsing: {ex.Message}", ex);
            }

            int i = 0;
            int n = lines.Length;
            while (i < n - 1)
            {
                string code = lines[i].Trim();
                string value = lines[i + 1].Trim();

                if (code == "0" && value == "MULTILEADER")
                {
                    int next = ParseOne(lines, i, result);
                    i = next;
                }
                else
                {
                    i += 2;
                }
            }

            return result;
        }

        // Parses one MULTILEADER entity starting at the "0"/"MULTILEADER" pair (index start).
        // Fails soft: a malformed entity is skipped (not added), never throws — one bad entity
        // must not abort reading the rest of the drawing. Returns the index to resume scanning.
        private static int ParseOne(string[] lines, int start, List<CalloutAnnotation> results)
        {
            int i = start + 2;
            int n = lines.Length;

            string layer = "0";
            string? text = null;
            var anchors = new List<Vector3>();
            var stack = new Stack<string>();
            bool inLeaderLine = false;
            double? px = null, py = null;

            try
            {
                while (i < n - 1)
                {
                    string code = lines[i].Trim();
                    string value = lines[i + 1].Trim();

                    if (code == "0")
                        break; // next entity begins

                    if (value.EndsWith("{", StringComparison.Ordinal))
                    {
                        stack.Push(value);
                        if (value == "LEADER_LINE{") inLeaderLine = true;
                    }
                    else if (value == "}")
                    {
                        if (stack.Count > 0)
                        {
                            string popped = stack.Pop();
                            if (popped == "LEADER_LINE{") inLeaderLine = false;
                        }
                    }
                    else if (code == "8" && stack.Count == 0)
                    {
                        layer = value;
                    }
                    else if (code == "304" && text == null &&
                             stack.Count > 0 && stack.Peek() == "CONTEXT_DATA{")
                    {
                        text = StripFormatting(value);
                    }
                    else if (inLeaderLine && code == "10")
                    {
                        px = double.Parse(value, CultureInfo.InvariantCulture);
                    }
                    else if (inLeaderLine && code == "20")
                    {
                        py = double.Parse(value, CultureInfo.InvariantCulture);
                    }
                    else if (inLeaderLine && code == "30")
                    {
                        double z = double.Parse(value, CultureInfo.InvariantCulture);
                        if (px.HasValue && py.HasValue)
                            anchors.Add(new Vector3(px.Value, py.Value, z));
                        px = null;
                        py = null;
                    }

                    i += 2;
                }
            }
            catch
            {
                // Malformed group codes for this entity — keep whatever partial data we have;
                // fall through to the same completeness check as the happy path.
            }

            if (!string.IsNullOrWhiteSpace(text) && anchors.Count > 0)
                results.Add(new CalloutAnnotation(layer, text!, anchors));

            return i;
        }

        // Same inline-formatting-code stripping as MTEXT (MULTILEADER content is an embedded
        // MTEXT fragment) — control sequences, paragraph breaks and brace grouping only.
        private static string StripFormatting(string raw)
        {
            string s = Regex.Replace(raw, @"\\[A-Za-z][^;]*;", "");
            s = s.Replace(@"\P", "\n").Replace(@"\p", "\n")
                 .Replace(@"\L", "").Replace(@"\l", "")
                 .Replace("{", "").Replace("}", "");
            return s.Trim();
        }
    }
}
