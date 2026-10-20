namespace WinOptimizer.Core.Features.Utilities;

public enum EnvironmentScope { User, System }

/// <summary>A persisted, unexpanded environment variable and its registry string type.</summary>
public sealed record EnvironmentVariableEntry(string Name, string Value, bool Expandable)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 255 || Name.Any(c => c == '=' || char.IsControl(c)))
            throw new ArgumentException("The variable name must contain 1–255 characters without '=' or control characters.");
        if (Value.Contains('\0') || Value.Length > 32766)
            throw new ArgumentException("The value must contain fewer than 32767 characters and no null characters.");
    }
}

public sealed record EnvironmentEditResult(bool NotificationDelivered);

/// <summary>Edits real user/system variables with optimistic conflict detection.</summary>
public interface IEnvironmentEditorService
{
    Task<IReadOnlyList<EnvironmentVariableEntry>> ReadAsync(EnvironmentScope scope);
    Task<EnvironmentEditResult> SaveAsync(EnvironmentScope scope, EnvironmentVariableEntry? original, EnvironmentVariableEntry value);
    Task<EnvironmentEditResult> DeleteAsync(EnvironmentScope scope, EnvironmentVariableEntry original);
}
