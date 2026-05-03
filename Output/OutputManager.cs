using System;
using System.Collections.Generic;
using System.Text;
using System.Collections;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Converters;
namespace MTTextClient.Output;

/// <summary>
/// Output mode for the REPL.
/// </summary>
public enum OutputMode
{
    Table,
    Json
}

/// <summary>
/// Manages output formatting based on current mode.
/// </summary>
public sealed class OutputManager
{
    public OutputMode Mode { get; set; } = OutputMode.Table;

    /// <summary>
    /// Format a CommandResult for display.
    /// </summary>
    public string Format(Commands.CommandResult result)
    {
        return Mode switch
        {
            OutputMode.Json => FormatJson(result),
            _ => FormatTable(result)
        };
    }

    private static string FormatJson(Commands.CommandResult result)
    {
        var output = new
        {
            success = result.Success,
            message = result.Message,
            data = result.Data
        };
        return JsonConvert.SerializeObject(output, Formatting.Indented, new StringEnumConverter());
    }

    private static string FormatTable(Commands.CommandResult result)
    {
        if (result.Data == null)
        {
            return result.Message;
        }

        var sb = new StringBuilder();
        sb.AppendLine(result.Message);
        sb.AppendLine();

        // If Data is a list, render as table
        if (result.Data is IList list && list.Count > 0)
        {
            RenderTable(sb, list);
        }
        else
        {
            // Single object — render as key/value pairs
            RenderObject(sb, result.Data);
        }

        return sb.ToString().TrimEnd();
    }

    private static void RenderTable(StringBuilder sb, IList list)
    {
        // Find the first non-null element to derive table headers from.
        // Lists from CommandResult.Data may legally contain null entries; in
        // that case we fall back to scalar formatting for the whole list.
        object? firstNonNull = null;
        foreach (object? candidate in list)
        {
            if (candidate != null) { firstNonNull = candidate; break; }
        }

        JObject firstObj;
        try
        {
            if (firstNonNull == null) throw new InvalidOperationException("all null");
            firstObj = JObject.FromObject(firstNonNull);
        }
        catch
        {
            foreach (object? item in list)
            {
                sb.AppendLine($"  {item}");
            }

            return;
        }

        List<string> headers = new List<string>();
        foreach (JProperty prop in firstObj.Properties())
        {
            headers.Add(prop.Name);
        }

        if (headers.Count == 0)
        {
            foreach (object? item in list)
            {
                sb.AppendLine($"  {item}");
            }

            return;
        }

        int colCount = headers.Count;
        int[] widths = new int[colCount];
        for (int i = 0; i < colCount; i++)
        {
            widths[i] = headers[i].Length;
        }

        List<string[]> rows = new List<string[]>();
        foreach (object? item in list)
        {
            string[] row = new string[colCount];
            if (item == null)
            {
                // Null row — render as "<null>" placeholder in first column,
                // empty cells in the rest. Avoids JObject.FromObject NRE
                // (EN review #16).
                row[0] = "<null>";
                for (int i = 1; i < colCount; i++) row[i] = "";
                widths[0] = Math.Max(widths[0], row[0].Length);
                rows.Add(row);
                continue;
            }

            JObject jObj;
            try
            {
                jObj = JObject.FromObject(item);
            }
            catch
            {
                // Non-serializable row — degrade to scalar repr in first cell.
                row[0] = item.ToString() ?? "";
                for (int i = 1; i < colCount; i++) row[i] = "";
                widths[0] = Math.Max(widths[0], row[0].Length);
                rows.Add(row);
                continue;
            }

            for (int i = 0; i < colCount; i++)
            {
                JToken? token = jObj[headers[i]];
                string val = CellToString(token);
                row[i] = val;
                widths[i] = Math.Max(widths[i], val.Length);
            }
            rows.Add(row);
        }

        for (int i = 0; i < colCount; i++)
        {
            widths[i] = Math.Min(widths[i], 40);
        }

        for (int i = 0; i < colCount; i++)
        {
            sb.Append(headers[i].PadRight(widths[i] + 2));
        }

        sb.AppendLine();

        for (int i = 0; i < colCount; i++)
        {
            sb.Append(new string('-', widths[i]));
            sb.Append("  ");
        }
        sb.AppendLine();

        foreach (string[] row in rows)
        {
            for (int i = 0; i < colCount; i++)
            {
                string val = row[i].Length > 40 ? row[i][..37] + "..." : row[i];
                sb.Append(val.PadRight(widths[i] + 2));
            }
            sb.AppendLine();
        }
    }

    private static void RenderObject(StringBuilder sb, object obj)
    {
        JObject jObj;
        try
        {
            jObj = JObject.FromObject(obj);
        }
        catch
        {
            sb.AppendLine($"  {obj}");
            return;
        }

        List<JProperty> props = new List<JProperty>();
        foreach (JProperty p in jObj.Properties())
        {
            props.Add(p);
        }

        if (props.Count == 0)
        {
            sb.AppendLine($"  {obj}");
            return;
        }

        int maxKeyLen = 0;
        for (int i = 0; i < props.Count; i++)
        {
            if (props[i].Name.Length > maxKeyLen)
            {
                maxKeyLen = props[i].Name.Length;
            }
        }
        for (int i = 0; i < props.Count; i++)
        {
            string val = CellToString(props[i].Value);
            if (val.Length > 120)
            {
                val = val[..117] + "...";
            }

            sb.AppendLine($"  {props[i].Name.PadRight(maxKeyLen + 1)}: {val}");
        }
    }

    /// <summary>
    /// Convert any cell value to a display string.
    /// Handles collections by joining elements, avoids "System.Collections..." output.
    /// </summary>
    private static string CellToString(object? rawVal)
    {
        if (rawVal == null)
        {
            return "";
        }

        // Handle JToken types from Newtonsoft
        if (rawVal is JToken token)
        {
            if (token.Type == JTokenType.Null)
            {
                return "";
            }

            if (token.Type == JTokenType.Array)
            {
                JArray arr = (JArray)token;
                List<string> items = new List<string>();
                foreach (JToken item in arr)
                {
                    items.Add(item.ToString());
                    if (items.Count >= 5)
                    {
                        break;
                    }
                }
                string joined = string.Join(", ", items);
                return items.Count >= 5 ? joined + ", ..." : joined;
            }
            if (token.Type == JTokenType.Object)
            {
                return token.ToString(Formatting.None);
            }

            return token.ToString();
        }

        if (rawVal is string s)
        {
            return s;
        }

        if (rawVal is IEnumerable enumerable)
        {
            List<string> items = new List<string>();
            foreach (object? item in enumerable)
            {
                items.Add(item?.ToString() ?? "");
                if (items.Count >= 5)
                {
                    break;
                }
            }
            string joined = string.Join(", ", items);
            return items.Count >= 5 ? joined + ", ..." : joined;
        }

        return rawVal.ToString() ?? "";
    }
}
