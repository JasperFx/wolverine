namespace Wolverine.Http.CodeGen;

internal static class CodeGenExtensions
{
    public static string SanitizeFormNameForVariable(this string variableName)
    {
        return variableName.Replace("/", "_").Replace("-", "_");
    }
}