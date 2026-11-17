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

    /// <summary>
    /// Android Release 等宿主上，插件 DLL 的 typeref（如 <c>IAsyncEnumerator&lt;&gt;</c>）可能无法解析；
    /// <see cref="Type.IsAssignableFrom"/> 会抛 <see cref="TypeLoadException"/>，必须吞掉以免闪退。
    /// </summary>
    public static bool IsSafeAssignableFrom(Type baseType, Type type)
    {
        try
        {
            return baseType.IsAssignableFrom(type);
        }
        catch (TypeLoadException)
        {
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception)
        {
            // Mono 偶发把解析失败包成其它异常
            return false;
        }
    }
}
