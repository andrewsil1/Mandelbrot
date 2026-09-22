# Analyze xperf's CSV dump with -add_fieldnames. Packet endpoint latency is
# NOT shader execution time: it includes queueing and completion reporting.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CsvPath,
    [Parameter(Mandatory)][int] $RendererPid,
    [Parameter(Mandatory)][string] $OutputPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName Microsoft.VisualBasic.Core
Add-Type -ReferencedAssemblies Microsoft.VisualBasic.Core,System.Collections,System.Runtime -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.VisualBasic.FileIO;

public static class RendererPacketAnalysis
{
    public sealed class QueueResult
    {
        public string Queue { get; set; }
        public int Node { get; set; }
        public int CompletedPackets { get; set; }
        public int PendingPackets { get; set; }
        public int DuplicateSubmissions { get; set; }
        public int UnmatchedCompletions { get; set; }
        public int FailedSubmissions { get; set; }
        public double EndpointLatencyTotalMs { get; set; }
        public double MedianMs { get; set; }
        public double P95Ms { get; set; }
        public double MaximumMs { get; set; }
    }
    private sealed class QueueData
    {
        public int Node;
        public readonly Dictionary<string, long> Pending = new Dictionary<string, long>();
        public readonly List<double> Latencies = new List<double>();
        public int Duplicates, Unmatched, Failed;
    }
    public static QueueResult[] Analyze(string path, int pid)
    {
        var contexts = new Dictionary<string, int>();
        var queues = new Dictionary<string, QueueData>();
        using (var reader = new StreamReader(path))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                // xperf's dump also contains diagnostic and non-CSV lines.
                // Only parse the three supported event families; malformed
                // supported records remain a hard failure, never silently lost.
                if (!line.StartsWith("Microsoft-Windows-DxgKrnl/Context/win:Start,", StringComparison.Ordinal)
                    && !line.StartsWith("Microsoft-Windows-DxgKrnl/HwQueue/win:Start,", StringComparison.Ordinal)
                    && !line.StartsWith("Microsoft-Windows-DxgKrnl/DmaPacket/win:Info,", StringComparison.Ordinal)) continue;
                string[] row;
                using (var parser = new TextFieldParser(new StringReader(line)))
                {
                    parser.TextFieldType = FieldType.Delimited;
                    parser.SetDelimiters(",");
                    parser.HasFieldsEnclosedInQuotes = true;
                    row = parser.ReadFields();
                }
                long timestamp;
                if (row.Length < 10 || !long.TryParse(row[1], out timestamp)) continue;
                // Payload labels supplied by xperf disambiguate the several
                // DmaPacket/Info schemas sharing the same event name.
                var fields = new Dictionary<string, string>();
                for (int i = 9; i < row.Length; i++)
                {
                    int colon = row[i].IndexOf(" : ", StringComparison.Ordinal);
                    if (colon >= 0) fields[row[i].Substring(0, colon)] = row[i].Substring(colon + 3);
                }
                string context, queue;
                bool owned = row[2] == "RendererChecks.exe (" + pid + ")";
                if (owned && row[0] == "Microsoft-Windows-DxgKrnl/Context/win:Start"
                    && fields.TryGetValue("hContext", out context))
                    contexts[context] = int.Parse(fields["NodeOrdinal"], CultureInfo.InvariantCulture);
                if (owned && row[0] == "Microsoft-Windows-DxgKrnl/HwQueue/win:Start"
                    && fields.TryGetValue("hContext", out context) && contexts.ContainsKey(context)
                    && fields.TryGetValue("ParentDxgHwQueue", out queue))
                    queues[queue] = new QueueData { Node = contexts[context] };
                QueueData data;
                string fence;
                if (row[0] != "Microsoft-Windows-DxgKrnl/DmaPacket/win:Info"
                    || !fields.TryGetValue("hHwQueue", out queue) || !queues.TryGetValue(queue, out data)
                    || !fields.TryGetValue("ProgressFenceValue", out fence)) continue;
                if (fields.ContainsKey("pDmaBuffer"))
                {
                    if (fields["ntStatus"] != "0") { data.Failed++; continue; }
                    if (data.Pending.ContainsKey(fence)) data.Duplicates++;
                    else data.Pending.Add(fence, timestamp);
                }
                else
                {
                    long start;
                    if (!data.Pending.TryGetValue(fence, out start)) { data.Unmatched++; continue; }
                    if (timestamp < start) throw new InvalidOperationException("Packet timestamp inversion.");
                    data.Latencies.Add((timestamp - start) / 1000.0);
                    data.Pending.Remove(fence);
                }
            }
        }
        var result = new List<QueueResult>();
        foreach (var pair in queues)
        {
            QueueData data = pair.Value;
            data.Latencies.Sort();
            int n = data.Latencies.Count;
            double total = 0;
            foreach (double value in data.Latencies) total += value;
            result.Add(new QueueResult { Queue = pair.Key, Node = data.Node, CompletedPackets = n,
                PendingPackets = data.Pending.Count, DuplicateSubmissions = data.Duplicates,
                UnmatchedCompletions = data.Unmatched, FailedSubmissions = data.Failed,
                EndpointLatencyTotalMs = total,
                MedianMs = n == 0 ? 0 : data.Latencies[(n - 1) / 2],
                P95Ms = n == 0 ? 0 : data.Latencies[(int)Math.Ceiling(n * 0.95) - 1],
                MaximumMs = n == 0 ? 0 : data.Latencies[n - 1] });
        }
        return result.ToArray();
    }
}
'@
$queues = [RendererPacketAnalysis]::Analyze([IO.Path]::GetFullPath($CsvPath), $RendererPid)
$report = [ordered]@{ rendererPid = $RendererPid; csv = [IO.Path]::GetFullPath($CsvPath);
    measurement = 'DXGKRNL submission-to-completion-report endpoint latency; not GPU execution or utilization';
    queues = @($queues) }
$report | ConvertTo-Json -Depth 5 | Out-File -LiteralPath $OutputPath -Encoding utf8
$queues | Format-Table -AutoSize
