# Internet and network controls

The Internet sidebar page exposes individually confirmed controls. Core's
`NetworkTuningCatalog` is the allowlist. `OptimizationToolsService` owns
batched discovery, baseline comparisons, writes and readback.
**Enhance network performance** applies normal TCP auto-tuning and enabled RSS.

Each tuning row displays the saved **Original** and **Suggested** values beside
each other. The first successful read of each setting is saved under
`%ProgramData%\win-optimizer\State\NetworkTuningOriginals` and survives edits,
reloads, and restarts. An absent registry override counts as a successful read.
The editor and write verification use the live Windows value. Suggestions use
general-purpose Windows settings where appropriate, keep the saved original
for workload- or policy-dependent controls, and suggest **No override** for
legacy priorities and the background download limit.

| Group | Controls |
| --- | --- |
| Delivery Optimization (1) | Background download limit in KB/s |
| TCP Internet template (8) | Receive-window auto-tuning; ECN; timestamps; scaling heuristics; non-SACK RTT resiliency; forced window scaling; initial retransmission timeout; SYN retries |
| Global offload (3) | Receive-side scaling; receive segment coalescing; task offload |
| IPv4 (5) | Default hop limit; neighbor-cache limit; route-cache limit; DHCP Media Sense; ICMP redirects |
| IPv6 (5) | Default hop limit; neighbor-cache limit; route-cache limit; DHCP Media Sense; ICMP redirects |
| Legacy resolution priorities (4) | LocalPriority; HostsPriority; DnsPriority; NetbtPriority |

The background limit stores `DOMaxBackgroundDownloadBandwidth` under
`HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization` on supported
Windows editions. A value of 0 requests dynamic adjustment. Clearing the card
removes the machine override. Readback verifies the stored policy. Managed
configuration and other Delivery Optimization policies can affect the effective
limit. The limit can free bandwidth for other traffic while background updates
are downloading.

Toggles use enabled/disabled native states. Auto-tuning and TCP timestamps have
explicit choices. **Allowed** uses timestamps for incoming connections when the
peer supports them. **Enabled** also negotiates timestamps for outgoing connections.
Numeric controls validate ranges before writing; initial RTO accepts 300–3000 ms
in increments of 10, SYN retries accept 2–8, and hop limits accept 1–255. Cache
limits accept positive UInt32 values. The getter properties `InitialRto`,
`NeighborCacheLimit` and `RouteCacheLimit` differ from their setter parameter names.

The four legacy priorities are DWORD overrides under
`HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider`. Lower numbers have
higher priority; edits accept 0–65535. Missing values show **No override**;
**Clear override** deletes only the selected value. Restart Windows after changes.
Registry readback confirms the stored value, not that a modern resolver uses it.
These entries do not select a DNS server or promise throughput/latency improvements.

Native source failures and unsupported properties disable the affected controls
with an explanation. Failed or externally changed edits require Refresh. Test mode
and configuration review permit discovery and block writes. These on-demand tools
do not participate in configuration presets/exports. The Internet page uses
the existing DNS setting workflow for adapter DNS choices and
`IConnectionService` to list Windows VPN profiles and open Windows Settings.

The on-demand speed test sends synthetic traffic to Cloudflare over HTTPS. It
estimates download and upload rates from timed, progressively larger transfers
with a combined payload cap below 70 MB. Idle latency is the median of five
empty HTTPS probes after an initial warm-up. Both rates describe the path to the
test endpoint under current conditions. The page keeps the latest two successful
measurements for the current app session.

## Validation

The test guide lists [Infrastructure filters](../../../../tests/README.md#domain-filters)
and [native UI commands](../../../../tests/README.md#focused-checks).

Infrastructure tests execute generated production scripts with fake native cmdlets
and registry handles and exercise the speed test with fake HTTP and time. The
native host runs the main UI view-model tests, exercises speed test results and
network edits on the production Internet page, and renders
English/Italian previews at 620 and 1300 pixels under `build/network-preview/`.
Both suites use fake Windows boundaries and make no host network changes.

## Native references

- [TCP settings and Windows client restrictions](https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-nettcpsetting)
- [TCP timestamp modes](https://learn.microsoft.com/en-us/windows/win32/fwp/wmi/nettcpipprov/msft-nettcpsetting)
- [Global offload](https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-netoffloadglobalsetting)
- [IPv4 protocol controls](https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-netipv4protocol)
- [IPv6 protocol controls](https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-netipv6protocol)
- [Network adapter tuning tradeoffs](https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics)
- [Delivery Optimization background download policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-deliveryoptimization#domaxbackgrounddownloadbandwidth)
- [Cloudflare speed test endpoints](https://github.com/cloudflare/speedtest#configuration)
