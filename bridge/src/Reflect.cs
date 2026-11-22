using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace ServiceStudioBridge;

/// <summary>Late-bound access to Service Studio's objects, by member name, so nothing is bound at compile time.</summary>
internal static class Reflect
{
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    public static Type FindType(string fullName, string assemblyName) =>
        Type.GetType($"{fullName}, {assemblyName}")
        ?? throw new BridgeException($"Type {fullName} isn't available: this process isn't Service Studio, or this Service Studio build changed it.");

    public static object? Static(Type type, string name)
    {
        // A static property re-declared with `new` makes a flattened lookup ambiguous: try the declaring type first.
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).FirstOrDefault(p => p.Name == name);
        return property?.GetValue(null);
    }

    /// <summary>Reads a public property or field; throws MissingMemberException when the type has neither.</summary>
    public static object? Get(object? target, string name)
    {
        if (target == null)
        {
            return null;
        }

        var type = target.GetType();
        var property = FindProperty(type, name);
        if (property != null)
        {
            return Unwrapped(() => property.GetValue(target));
        }

        var field = type.GetField(name, PublicInstance);
        if (field != null)
        {
            return field.GetValue(target);
        }

        throw new MissingMemberException(type.FullName, name);
    }

    public static object? TryGet(object? target, string name)
    {
        try
        {
            return Get(target, name);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Calls the public overload that best fits the arguments' runtime types, including explicit interface implementations.</summary>
    public static object? Call(object target, string name, params object?[] args)
    {
        var type = target.GetType();
        var best = type.GetMethods(PublicInstance)
            .Concat(type.GetInterfaces().SelectMany(i => i.GetMethods()))
            .Where(m => m.Name == name && !m.IsGenericMethodDefinition)
            .Select(m => (Method: m, Score: Score(m.GetParameters(), args)))
            .Where(c => c.Score >= 0)
            .OrderByDescending(c => c.Score)
            .Select(c => c.Method)
            .FirstOrDefault()
            ?? throw new MissingMethodException(type.FullName, name);
        return Unwrapped(() => best.Invoke(target, args));
    }

    public static object? TryCall(object? target, string name, params object?[] args)
    {
        try
        {
            return target == null ? null : Call(target, name, args);
        }
        catch
        {
            return null;
        }
    }

    public static List<object> Enumerate(object? value) =>
        value is IEnumerable sequence and not string ? sequence.Cast<object?>().Where(x => x != null).Select(x => x!).ToList() : [];

    /// <summary>The named members the object has, in order; members it lacks (or that throw) are left out.</summary>
    public static Dictionary<string, object?> Pick(object source, params string[] names)
    {
        var picked = new Dictionary<string, object?>();
        foreach (var name in names)
        {
            try
            {
                picked[name] = Get(source, name);
            }
            catch
            {
                // Missing in this build: leave it out.
            }
        }

        return picked;
    }

    public static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException or AggregateException && exception.InnerException != null)
        {
            exception = exception.InnerException;
        }

        return exception;
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var property = t.GetProperty(name, PublicInstance | BindingFlags.DeclaredOnly);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return property;
            }
        }

        return type.GetInterfaces()
            .Select(i => i.GetProperty(name))
            .FirstOrDefault(p => p != null && p.GetIndexParameters().Length == 0);
    }

    private static int Score(ParameterInfo[] parameters, object?[] args)
    {
        if (parameters.Length != args.Length)
        {
            return -1;
        }

        var score = 0;
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameterType = parameters[i].ParameterType;
            if (parameterType.IsByRef)
            {
                return -1;
            }

            var arg = args[i];
            if (arg == null)
            {
                if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) == null)
                {
                    return -1;
                }

                continue;
            }

            if (!parameterType.IsInstanceOfType(arg))
            {
                return -1;
            }

            score += parameterType == arg.GetType() ? 3 : parameterType == typeof(object) ? 0 : 1;
        }

        return score;
    }

    private static object? Unwrapped(Func<object?> body)
    {
        try
        {
            return body();
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
