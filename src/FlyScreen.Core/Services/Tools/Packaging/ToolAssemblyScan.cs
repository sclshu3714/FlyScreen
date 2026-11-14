using System.Reflection;

namespace FlyScreen.Services.Tools;

/// <summary>安全枚举程序集类型（ALC 探测包常缺依赖，GetTypes 会抛 <see cref="ReflectionTypeLoadException"/>）。</summary>
internal static class ToolAssemblyScan
{
    public static IEnumerable<Type> GetLoadableTypes(Assembly asm)
    {
        Type?[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }
        catch
        {
            yield break;
        }

        foreach (var t in types)
        {
            if (t != null)
                yield return t;
        }
    }
}
