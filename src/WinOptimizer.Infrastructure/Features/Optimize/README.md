# Network controls

Optimize → Network exposes 25 individually confirmed controls. Core's
`NetworkTuningCatalog` is the allowlist; `OptimizationToolsService.Network` owns
batched discovery, baseline comparisons, writes and readback. The existing quick
enhancement applies normal TCP auto-tuning and enabled RSS only.

| Group | Controls |
| --- | --- |
| TCP Internet template (8) | Receive-window auto-tuning; ECN; timestamps; scaling heuristics; non-SACK RTT resiliency; forced window scaling; initial retransmission timeout; SYN retries |
| Global offload (3) | Receive-side scaling; receive segment coalescing; task offload |
| IPv4 (5) | Default hop limit; neighbor-cache limit; route-cache limit; DHCP Media Sense; ICMP redirects |
| IPv6 (5) | Default hop limit; neighbor-cache limit; route-cache limit; DHCP Media Sense; ICMP redirects |
| Legacy resolution priorities (4) | LocalPriority; HostsPriority; DnsPriority; NetbtPriority |

Toggles use enabled/disabled native states. Auto-tuning has explicit choices.
Numeric controls validate ranges before writing; initial RTO accepts 300–3000 ms
in increments of 10, SYN retries accept 2–8, and hop limits accept 1–255. Cache
limits accept positive UInt32 values. The getter properties `InitialRto`,
`NeighborCacheLimit` and `RouteCacheLimit` differ from their setter parameter names.

The four legacy priorities are DWORD overrides under
`HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider`. Lower numbers have
higher priority. The example values 4/5/6/7 are available as explicit numeric edits;
they are not applied automatically. Missing values show **No override**;
**Clear override** deletes only the selected value. Restart Windows after changes.
Registry readback confirms the stored value, not that a modern resolver uses it.
These entries do not select a DNS server or promise throughput/latency improvements.

Native source failures and unsupported properties disable the affected controls
with an explanation. Failed or externally changed edits require Refresh. Test mode
and configuration review permit discovery and block writes. These on-demand tools
do not participate in configuration presets/exports. DNS server choices and
adapter-specific settings retain their existing owners.

## Validation

Commands are in the [test guide](../../../../tests/README.md#focused-checks).

Infrastructure tests execute generated production scripts with fake native cmdlets
and registry handles. The native host runs the main UI view-model tests, exercises
the production Network page's toggles, selector and number fields, and renders
English/Italian previews at 620 and 1300 pixels under `build/network-preview/`.
Both suites use fake Windows boundaries and make no host network changes.

## Native references

- [TCP settings and Windows client restrictions](https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-nettcpsetting)
- [Global offload](https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-netoffloadglobalsetting)
- [IPv4 protocol controls](https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-netipv4protocol)
- [IPv6 protocol controls](https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-netipv6protocol)
- [Network adapter tuning tradeoffs](https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics)
