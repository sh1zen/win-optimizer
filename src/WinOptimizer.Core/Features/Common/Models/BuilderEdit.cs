using System.Collections.Generic;
using WinOptimizer.Core.Features.Common.Enums;

namespace WinOptimizer.Core.Features.Common.Models;

/// <summary>
/// A single setting change recorded during a Builder-mode session. Builder Save
/// merges these onto the system-seeded base configuration so the saved file reflects
/// the user's authored intent rather than only the live system state.
/// </summary>
public class BuilderEdit
{
    public string SettingId { get; set; } = string.Empty;
    public InputType InputType { get; set; }

    /// <summary>For Toggle / CheckBox / Action: the recorded on/off (or "include") state.</summary>
    public bool? IsSelected { get; set; }

    /// <summary>For Selection on a predefined option: the chosen combo-box index.</summary>
    public int? SelectedIndex { get; set; }

    /// <summary>For Selection seeded at the Custom index: the raw values to write.</summary>
    public Dictionary<string, object>? CustomStateValues { get; set; }

    /// <summary>Power values in system units, or AC/DC option indexes for selections.</summary>
    public Dictionary<string, object>? PowerSettings { get; set; }
}
