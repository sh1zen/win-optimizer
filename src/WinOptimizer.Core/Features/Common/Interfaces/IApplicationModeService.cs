using System;
using System.Collections.Generic;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

/// <summary>
/// Owns the app-wide interaction mode. Single source of truth, registered as a
/// singleton. Exposes the current mode so
/// Normal / Builder / ConfigReview can be distinguished.
/// </summary>
public interface IApplicationModeService
{
    /// <summary>The current app-wide mode. Defaults to <see cref="WinOptimizerMode.Normal"/>.</summary>
    WinOptimizerMode CurrentMode { get; }

    /// <summary>
    /// The active Builder target. Only meaningful while <see cref="CurrentMode"/> is
    /// <see cref="WinOptimizerMode.Builder"/>. Defaults to <see cref="BuilderTarget.Config"/>.
    /// </summary>
    BuilderTarget CurrentBuilderTarget { get; }

    /// <summary>Raised whenever <see cref="CurrentMode"/> changes.</summary>
    event EventHandler? ModeChanged;

    /// <summary>Restores presentation baselines before a test draft is discarded or handed to another mode.</summary>
    event EventHandler? BuilderModeExiting;

    /// <summary>True while a real configuration operation is in progress.</summary>
    bool IsSystemOperationRunning { get; }
    event EventHandler? SystemOperationChanged;

    /// <summary>
    /// Reserves a real system operation until disposed and prevents test-mode entry.
    /// Throws when test mode is active. Nested setting operations share the reservation.
    /// </summary>
    IDisposable BeginSystemOperation();

    /// <summary>
    /// Enter Builder mode with the given target, retaining the current settings
    /// catalogue and displayed values. No system writes occur while in Builder mode.
    /// </summary>
    void EnterBuilderMode(BuilderTarget target);

    /// <summary>
    /// Switch the Builder target without leaving Builder mode. Authored UI state
    /// is preserved; only card visibility and Save output change. No-op if not in
    /// Builder mode.
    /// </summary>
    void SetBuilderTarget(BuilderTarget target);

    /// <summary>
    /// Return to Normal mode from Builder (or any non-review mode). Config Review
    /// has its own exit path (<see cref="IConfigReviewModeService.ExitReviewMode"/>).
    /// </summary>
    void EnterNormalMode();

    /// <summary>
    /// Record (upsert by SettingId) a setting change made during the current Builder
    /// session. No-op semantics outside Builder are the caller's responsibility.
    /// </summary>
    void RecordBuilderEdit(BuilderEdit edit);

    /// <summary>
    /// The edits recorded during the current Builder session. Cleared when entering
    /// Builder, returning to Normal, or exiting Review.
    /// </summary>
    IReadOnlyCollection<BuilderEdit> GetBuilderEdits();
}
