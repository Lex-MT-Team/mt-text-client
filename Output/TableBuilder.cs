using System;
using System.Collections.Generic;
using System.Text;
namespace MTTextClient.Output;

/// <summary>
/// Builds a text table from explicit column headers and string rows.
/// No reflection required.
/// </summary>
public sealed class TableBuilder
{
    private readonly string[] _headers;
    private readonly List<string[]> _rows;

    public TableBuilder(params string[] headers)
    {
        _headers = headers;
        _rows = new List<string[]>();
    }

    public void AddRow(params string[] values)
    {
        _rows.Add(values);
    }

    public override string ToString()
    {
        if (_rows.Count == 0)
        {
            return "(empty)";
        }

        int colCount = _headers.Length;
        int[] widths = new int[colCount];
        for (int i = 0; i < colCount; i++)
        {
            widths[i] = _headers[i].Length;
        }

        for (int r = 0; r < _rows.Count; r++)
        {
            string[] row = _rows[r];
            for (int i = 0; i < colCount && i < row.Length; i++)
            {
                int len = (row[i] ?? "").Length;
                if (len > widths[i])
                {
                    widths[i] = len;
                }
            }
        }

        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < colCount; i++)
        {
            sb.Append(_headers[i].PadRight(widths[i] + 2));
        }

        sb.AppendLine();

        int totalWidth = 0;
        for (int i = 0; i < colCount; i++)
        {
            totalWidth += widths[i];
        }

        sb.AppendLine(new string('-', totalWidth + colCount * 2));

        for (int r = 0; r < _rows.Count; r++)
        {
            string[] row = _rows[r];
            for (int i = 0; i < colCount; i++)
            {
                string val = (i < row.Length ? row[i] : "") ?? "";
                sb.Append(val.PadRight(widths[i] + 2));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
