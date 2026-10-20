using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace WinOptimizer.UI.Tests.Helpers;

/// <summary>Runs fact and inline-data cases on the native host's current synchronization context.</summary>
public static class NativeTestCases
{
    /// <summary>Creates and disposes a fixture per case, retaining the failing test's identity.</summary>
    public static async Task<int> RunAsync(params Type[] types)
    {
        var count = 0;
        foreach (var type in types)
        foreach (var method in type.GetMethods().Where(method => method.IsDefined(typeof(FactAttribute))))
        {
            var data = method.GetCustomAttributes<InlineDataAttribute>().SelectMany(attribute => attribute.GetData(method)).ToArray();
            foreach (var row in data.Length == 0 ? new[] { Array.Empty<object>() } : data)
            {
                var instance = Activator.CreateInstance(type);
                try
                {
                    if (method.Invoke(instance, row) is Task task) await task;
                    count++;
                }
                catch (TargetInvocationException ex) { throw new InvalidOperationException(type.Name + "." + method.Name, ex.InnerException); }
                finally { (instance as IDisposable)?.Dispose(); }
            }
        }
        return count;
    }
}
