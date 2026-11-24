# Battery charge providers

`BatteryChargeService` discovers the manufacturer and installed batteries, queries
the matching `IBatteryFirmwareProvider`, validates the requested percentage, applies
the setting and rereads it. Only matching firmware readback confirms the settings.
When Windows reports one battery, the service also checks whether it is still
charging above the reported limit. The page checks that condition when opened
and after applying a limit. Discovery does not apply settings. Writes require
normal application mode and the appropriate firmware/Windows permissions.

## Battery readings

Every discovery reads active standard Windows battery WMI classes through the same
boundary. `BatteryTelemetryReader` joins static data, full capacity, status, cycle
count and temperature by `InstanceName`, keeping batteries separate even when
providers return different orders or incomplete data. Failures identify the source
and do not disable independent OEM charging controls. Missing sensors and unknown
sentinels remain unavailable. A zero cycle count does not establish counter support.

Capacity and rates are converted to Wh/W only when capabilities establish absolute
units. Voltage is converted from mV to V, and battery temperature from tenths of
Kelvin to Celsius. Health is full capacity divided by design capacity; wear is the
nonnegative loss from 100%. Current is estimated from the active power flow and
voltage. Runtime is remaining energy divided by discharge power while discharging;
it varies with workload. These readings do not measure total AC wall power.
The page displays the reading time. Initial discovery, support rechecks and applying
a charge limit collect fresh readings. Charge-limit controls sit above battery
details and power readings. The selector offers firmware-supported percentages:
60%, 70%, and 90% are available on compatible Dell custom ranges and ASUS endpoints.
Acer's health mode provides fixed 80%/100% choices.

Windows contracts: [static battery data](https://learn.microsoft.com/en-us/windows/win32/api/batclass/ns-batclass-battery_wmi_static_data),
[status and rate units](https://learn.microsoft.com/en-us/windows/win32/power/battery-status-str),
[relative capacity and cycle counters](https://learn.microsoft.com/en-us/windows/win32/power/battery-information-str),
[temperature units](https://learn.microsoft.com/en-us/windows/win32/power/battery-query-information-str).

| Manufacturer | Existing endpoint | Offered limits |
| --- | --- | --- |
| Dell | Agentless BIOS WMI attributes | Advertised custom ranges and Standard mode |
| HP | Instrumented BIOS Battery Health Manager | Advertised fixed 80%/100% modes |
| ASUS | Installed System Control Interface driver | Readable charge-limit endpoint |
| Acer | `root\wmi:BatteryControl` | Fixed 80%/100% when health mode is advertised |
| Lenovo, MSI, Samsung, LG, Surface | OEM app installation guidance | Charging controls in the manufacturer's app on supported models |
| Other manufacturers | Manufacturer guidance | No direct control without a compatible provider |

## Restart threshold

Dell's Custom mode provides separate start and stop thresholds. The **Resume
charging** selector offers fixed levels below the stop limit, relative options
10 or 20 percentage points below it, and **Soonest supported** for the highest
accepted restart threshold. The choices follow the advertised start range
and increment and must be at least five points below the selected stop limit.
For example, with an 80% stop limit, 20 points below the limit sets a 60% restart
threshold. A 100% stop limit can use **100% (Standard charging)** or a supported restart
threshold in Custom mode. Apply verifies firmware readback, and the firmware retains
the settings after the app closes. See [Dell's charging attribute reference](https://dl.dell.com/topicspdf/command-powershell-provider_reference-guide4_en-us.pdf).

## Installing OEM support

`BatterySupportCatalog` owns official installation routes, package names and help
links. `BatteryChargeState.Issue` distinguishes an absent interface, unsupported or
locked firmware, access denial, failed discovery and a manufacturer without a direct
provider. A recommendation is independent of `SupportedLimits` and cannot enable writes.
For ASUS, device-open errors 2/3 mean the existing driver endpoint is unavailable;
error 5 means access denial, and other errors retain their diagnostics.

Select **Install OEM support…** to open the catalogued official route and choose
the exact model and Windows version. **Optional driver updates** opens Windows
Settings for relevant driver-based routes; firmware-only paths use **Check model
support…**. Access denial, locked settings and read errors show corrective guidance
without installation actions. Unknown manufacturers receive no package suggestion.

Installation actions are unavailable in Test/review modes. Recheck is read-only,
reruns discovery, and removes the helper only when firmware control is detected.
Devices without a direct provider use their OEM app's charging controls where
supported. Installing OEM software does not establish firmware capability.

OEM references: [ASUS](https://www.asus.com/support/faq/1045651/),
[Lenovo ACPI driver recovery](https://support.lenovo.com/us/en/solutions/ht513315),
[HP firmware support](https://support.hp.com/us-en/document/c06465959),
[MSI Battery Master](https://us.msi.com/support/technical_details/NB_Laptop_Battery),
[Samsung Settings](https://www.samsung.com/us/support/galaxy-battery/galaxy-book-battery/),
[LG](https://www.lg.com/us/support/help-library/lg-laptop-battery-battery-is-charged-only-up-to-80--20154629826640),
[Surface app installation](https://support.microsoft.com/en-us/surface/battery/smart-charging-on-surface).

## Acer protocol

`AcerBatteryFirmware` uses the existing `root\wmi:BatteryControl` endpoint through
the shared WMI boundary:

1. Query active `BatteryControl` instances. Prefer a unique APGe instance, otherwise
   require a single active instance. Reject ambiguous or unnamed endpoints.
2. Invoke `GetBatteryHealthControlStatus` on the selected instance with byte-valued
   `uBatteryNo=1`, `uFunctionQuery=1`, and two zero reserved bytes.
3. Require bit `0x01` in `uFunctionList`. Decode `uFunctionStatus`, a five-byte array:
   the first byte being nonzero means 80% health mode; zero means full charging.
4. Invoke `SetBatteryHealthControl` with `uBatteryNo=1`, `uFunctionMask=1`,
   `uFunctionStatus=1` for 80% or `0` for 100%, and five zero `uReservedIn` bytes.
   Calibration and other firmware functions are independent of this mask.
5. Require a zero `uReturn` value from `SetBatteryHealthControl` and reread
   health mode through the service before confirming the requested setting.

The protocol addresses battery number 1. Its firmware capabilities and permission
requirements vary by model. The endpoint being present does not establish that
health mode is supported. A failed read leaves direct control unavailable and
publishes the diagnostic. OEM tools or firmware resets can change the setting later.
No service configuration or driver installation is part of this workflow.

References: [Acer's feature documentation](https://community.acer.com/en/kb/articles/19027-battery-charge-limit-function-on-acer-notebooks),
[Acer WMI battery driver](https://github.com/torvalds/linux/blob/master/drivers/platform/x86/acer-wmi-battery.c).

## Cross-manufacturer limits

Windows [Smart charging is manufacturer-specific](https://support.microsoft.com/en-us/windows/experience/power-battery/use-smart-charging-in-windows).
Power-plan battery thresholds configure low-battery actions, not a charge cap.
Extending coverage requires an OEM protocol, capability discovery and verified
readback; brand detection alone cannot authorize writes.
