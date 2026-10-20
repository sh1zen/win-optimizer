# Process monitor

Open **Control → Process monitor**. The page samples while visible, with an
expandable parent tree and search. Click **Process**, **PID**, **CPU** or **Memory**
to sort the flat list; click the same header again to reverse direction. An arrow
marks the active order. CPU and memory start largest-first; names and PIDs start
ascending. Unavailable measurements stay last, with name and process identity
breaking ties. Live updates and search retain the selected order and process.
Choose **Process tree** in the order selector to restore the hierarchy and its
expanded/collapsed branches. Numeric columns align to the right; CPU bars stay
within the CPU column. Select a row for
details, ports, open-file references and explicitly started traffic observation.
The monitor observes processes; it does not terminate/suspend them or close handles.

## Measurements and coverage

- A process identity includes PID and Windows creation time. The service checks
  that identity around selected-process reads and capture. Reused PIDs cannot
  inherit a previous process's details or traffic. Exited selections retain dated
  snapshots and disable new queries.
- CPU uses cumulative kernel/user time over the measured sampling interval,
  normalized by all logical processors. The first sample, inaccessible counters,
  resets and gaps over ten seconds show an unavailable rate. Working set and
  private bytes are separate measurements. I/O rates are general process I/O;
  they include sources other than disk and are not network totals.
- Command line and owner come from a selected-process WMI query; description and
  publisher are executable version metadata, not verified digital signatures.
  Protected/system processes remain visible with diagnostics and unknown values.
- Ports use IP Helper's owning-PID TCP/UDP tables for IPv4 and IPv6. TCP includes
  state and remote endpoint; a UDP binding has no remote peer in those tables.
  Ports refresh while selected. No DNS lookups are performed.
- Open files are an explicit, dated Microsoft Handle query. Only selected-PID
  `File` records are displayed, including non-disk file references reported by
  Windows. This is not an inventory of every memory-mapped image or historical
  access. Unexpected CSV and native failures report errors rather than an empty
  success. Access restrictions can limit coverage.

## Observed traffic

**Start observation** requires administrator rights and creates one uniquely named,
owned real-time ETW session through Microsoft TraceEvent. Typed TCP/UDP
send/receive events cover IPv4 and IPv6; TraceEvent uses the event payload's process
identity. Event sizes aggregate by protocol and remote IP for the selected process
from that observation's start. This is observed transport traffic, not retained
Windows history, interface wire-byte accounting or traffic from before observation.
Retransmit/copy events are not separately added.

Local adapter addresses identify the remote endpoint and refresh during observation.
Ambiguous tuples are counted as unattributed; they never invent a remote host.
Same-address loopback tuples remain attributable. Distinct local addresses on a
local-to-local connection may be ambiguous. Up to 4096 remote-IP/protocol pairs are
retained; extra events contribute to the visible capacity/unattributed indication.
The page reports ETW session lost-event counts (or unavailable), attribution gaps
and capture failures. A zero observed total does not establish complete coverage.

No packet payloads or ETL files are collected. Totals stay in bounded application
memory. Stop, selection change, page navigation and shutdown dispose the owned
session; shutdown drains pending native queries. Other applications' sessions are
not stopped. Process-lifetime validation before and after reads prevents publishing
new events after identity loss; the last validated snapshot remains available.

## Optional Microsoft tool

Open-file queries require administrator rights and explicit acceptance of the
[Sysinternals license](https://learn.microsoft.com/en-us/sysinternals/license-terms)
and download. The shared authenticated Management script runner acquires Handle
from Microsoft's `https://download.sysinternals.com/files/Handle.zip` on demand,
extracts only `handle64.exe`, validates a valid Microsoft Corporation Authenticode
signature and retains a read lock while executing it. Its cache is
`%ProgramData%/win-optimizer/Tools/handle`; no executable is bundled. Reparse paths
and unexpected cache locations are rejected. The command requests CSV for one PID
(`-accepteula -nobanner -v -p PID`); no handle-close option is exposed.
Downloaded versions follow Microsoft's current distribution and are not pinned.

## Validation

- Core: `ProcessMetricTests` covers baselines, rates and process-parent graphs.
- Infrastructure: `ProcessMonitorServiceTests`, `ProcessTrafficLedgerTests`,
  `ProcessPortsAndFilesTests` and `ManagementPowerShellRunnerTests` exercise fake
  OS boundaries, native table bytes, quoted CSV, session lifetime and authenticated
  embedded script composition. Existing Autorun tests cover the shared transport.
- The [native UI suite](../../../../tests/README.md#focused-checks) builds the actual WinUI page and runs
  view-model cases plus EN/IT, light/dark, three widths, all four tabs and short
  windows. Previews are under `build/process-monitor-preview`. Data is synthetic:
  these tests do not enumerate live processes, download Handle or start ETW.

Native acquisition, protected-process coverage and ETW availability depend on the
Windows host and privileges and require integration verification on that host.

## Microsoft references

- [Handle options and permissions](https://learn.microsoft.com/en-us/sysinternals/downloads/handle)
- [TraceEvent source and documentation](https://github.com/microsoft/perfview/tree/main/src/TraceEvent)
- [TCP/IP ETW provider](https://learn.microsoft.com/en-us/windows/win32/etw/tcpip)
- [Owning-PID TCP tables](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable)
- [Owning-PID UDP tables](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedudptable)
