using System.Text;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>
/// Every source file must be valid UTF-8. One stray byte in another encoding makes the compiler read the whole
/// file in the machine's code page, which garbles every non-ASCII character in it (·, …, →) in the running app.
/// </summary>
public class SourceEncodingTests
{
    [Fact]
    public void Every_source_file_is_valid_utf8()
    {
        // From this file's own path: the tests may run from a build folder outside the repository.
        var root = new FileInfo(ThisFile()).Directory;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Orrery.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var invalid = new List<string>();
        foreach (var folder in new[] { "src", "tests" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root.FullName, folder), "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal))
                         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            {
                try
                {
                    strict.GetString(File.ReadAllBytes(file));
                }
                catch (DecoderFallbackException)
                {
                    invalid.Add(Path.GetRelativePath(root.FullName, file));
                }
            }
        }

        Assert.Empty(invalid);
    }

    static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
