using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

/// <summary>
/// Resolves and caches artwork from installed AppX packages in current-user,
/// all-user and provisioned scopes, and from registered desktop icon resources.
/// Entries without local artwork keep IconPath null so the UI renders their category glyph.
/// </summary>
public interface IAppIconResolver
{
    /// <summary>
    /// Walks the given definitions, extracts package and registered desktop icons,
    /// and stamps ItemDefinition.IconPath on success. Failures are logged and
    /// swallowed — IconPath stays null on any per-entry or batch-level failure.
    /// </summary>
    /// <param name="applyThemeAdaptation">
    /// When true (Windows Apps), cached package icons get theme adaptation: uniform
    /// backplates are cropped and monochrome icons get synthesized light/dark
    /// variants. When false, artwork keeps its original colors and backplate;
    /// only the basic transparent-border trim is applied. Desktop icons preserve
    /// their registered colors.
    /// </param>
    Task ResolveBatchAsync(
        IEnumerable<ItemDefinition> definitions,
        bool applyThemeAdaptation = true,
        CancellationToken ct = default);

    /// <summary>Resolves icons using the desktop registrations captured by the current inventory.</summary>
    Task ResolveBatchAsync(IEnumerable<ItemDefinition> definitions, IReadOnlyList<IRegisteredDesktopIcon> registrations,
        bool applyThemeAdaptation = true, CancellationToken ct = default) => ResolveBatchAsync(definitions, applyThemeAdaptation, ct);
}

/// <summary>Identifies the desktop registration and icon reference used by the artwork resolver.</summary>
public interface IRegisteredDesktopIcon
{
    string KeyPath { get; }
    string? DisplayIcon { get; }
    bool Matches(ItemDefinition definition);
}
