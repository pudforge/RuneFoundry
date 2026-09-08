using System.Text.Json;

namespace RuneFoundry.Core;

/// <summary>
/// Installing a mod writes into Program Files, which needs administrator rights this app
/// deliberately does not hold. Holding them would cost more than it buys: a process
/// started by an elevated parent inherits that token, and the game cannot reach a
/// Battle.net session with it, so running the whole program as administrator means not
/// being able to play what you just installed.
///
/// So only the copying elevates. This starts the same executable again with a switch and
/// the "runas" verb, which is what raises the UAC prompt; the child does the install,
/// writes down what it did, and exits. Windows asks once, at the moment files are
/// actually written, and the app itself stays an ordinary program.
/// </summary>
public static class ElevatedApply
{
    /// <summary>The switch the helper is started with. Not a supported entry point.</summary>
    public const string Switch = "--apply-elevated";

    /// <summary>Windows' code for a UAC prompt the user dismissed.</summary>
    private const int ErrorCancelled = 1223;

    public sealed class Request
    {
        public string GameRoot { get; set; } = "";
        public string VaultRoot { get; set; } = "";

        /// <summary>
        /// Who asked. Usually the same account as the helper's, but not when a standard
        /// user elevates by typing an administrator's credentials — and it is the caller
        /// who has to be able to write the vault afterwards.
        /// </summary>
        public string? CallerSid { get; set; }

        /// <summary>The mod to make the only enabled one; null is vanilla.</summary>
        public string? ModId { get; set; }
    }

    /// <summary>
    /// What the helper did. The same fields as <see cref="ApplyResult"/>, because that is
    /// what the caller wants back — it just cannot cross a process boundary as itself.
    /// </summary>
    public sealed class Response
    {
        public bool Succeeded { get; set; }
        public string? FailureMessage { get; set; }
        public int FilesWritten { get; set; }
        public int FilesRestored { get; set; }
        public int FilesDeleted { get; set; }
        public List<string> Warnings { get; set; } = new();

        public ApplyResult ToResult()
        {
            var result = new ApplyResult
            {
                Succeeded = Succeeded,
                FailureMessage = FailureMessage,
                FilesWritten = FilesWritten,
                FilesRestored = FilesRestored,
                FilesDeleted = FilesDeleted,
            };
            result.Warnings.AddRange(Warnings);
            return result;
        }
    }

    /// <summary>
    /// Runs the install in an elevated copy of this program and waits for it.
    /// Null means the user dismissed the prompt: the helper never ran, so nothing changed.
    /// </summary>
    public static Response? Run(string gameRoot, string vaultRoot, string? modId)
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("Cannot find this program's own path.");

        // Passed in a file rather than on the command line: paths are the kind of thing
        // that ends up with a quote in it, and the argument would be visible to anything
        // listing processes.
        var requestPath = Path.Combine(Path.GetTempPath(), $"war2mod-apply-{Guid.NewGuid():N}.json");
        var responsePath = ResponsePathFor(requestPath);

        using var caller = System.Security.Principal.WindowsIdentity.GetCurrent();

        File.WriteAllText(requestPath, JsonSerializer.Serialize(
            new Request
            {
                GameRoot = gameRoot,
                VaultRoot = vaultRoot,
                ModId = modId,
                CallerSid = caller.User?.Value,
            }));

        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = $"{Switch} \"{requestPath}\"",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                });

            if (process is null) throw new InvalidOperationException("The helper did not start.");
            process.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
        finally
        {
            TryDelete(requestPath);
        }

        try
        {
            if (!File.Exists(responsePath))
                return new Response
                {
                    Succeeded = false,
                    FailureMessage = "The elevated helper exited without reporting what it did. "
                                     + "The game folder may be half-written; check it before playing.",
                };

            return JsonSerializer.Deserialize<Response>(File.ReadAllText(responsePath))
                   ?? new Response { Succeeded = false, FailureMessage = "The helper reported nothing." };
        }
        finally
        {
            TryDelete(responsePath);
        }
    }

    /// <summary>The elevated side: does the install, writes the result, exits.</summary>
    public static int Execute(string requestPath)
    {
        var response = new Response();

        try
        {
            var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath))
                          ?? throw new InvalidOperationException("The request file was empty.");

            if (!GameInstall.TryOpen(request.GameRoot, out var game, out var error))
                throw new InvalidOperationException(error);

            var vault = new BackupVault(request.VaultRoot);
            EnsureVaultWritable(vault.Root, request.CallerSid);

            var installer = new ModInstaller(game!, vault);
            installer.SetOnlyEnabled(request.ModId);

            var result = installer.Apply();

            response.Succeeded = result.Succeeded;
            response.FailureMessage = result.FailureMessage;
            response.FilesWritten = result.FilesWritten;
            response.FilesRestored = result.FilesRestored;
            response.FilesDeleted = result.FilesDeleted;
            response.Warnings.AddRange(result.Warnings);
        }
        catch (Exception ex)
        {
            response.Succeeded = false;
            response.FailureMessage = $"{ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            File.WriteAllText(ResponsePathFor(requestPath), JsonSerializer.Serialize(response));
        }
        catch
        {
            // Nothing to report it to. The caller treats the silence as a failure.
        }

        return response.Succeeded ? 0 : 1;
    }

    public static string ResponsePathFor(string requestPath) => requestPath + ".result";

    /// <summary>
    /// Keeps the vault writable by the ordinary user.
    ///
    /// ProgramData's default rules give a new file full control to whoever created it and
    /// read-only to everyone else, so anything this helper writes is a file the unelevated
    /// app can never write again. That is how a state.json ends up read-only and mod
    /// changes silently stop being remembered. Granting the caller modify rights on the
    /// vault — inheritably, and directly on what is already there — is the repair.
    ///
    /// Only ever widens access, and only within the loader's own folder.
    /// </summary>
    private static void EnsureVaultWritable(string vaultRoot, string? callerSid)
    {
        try
        {
            System.Security.Principal.IdentityReference user;
            if (!string.IsNullOrWhiteSpace(callerSid))
            {
                user = new System.Security.Principal.SecurityIdentifier(callerSid);
            }
            else
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                if (identity.User is null) return;
                user = identity.User;
            }

            var rule = new System.Security.AccessControl.FileSystemAccessRule(
                user,
                System.Security.AccessControl.FileSystemRights.Modify,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit
                    | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow);

            var directory = new DirectoryInfo(vaultRoot);
            var security = directory.GetAccessControl();
            security.AddAccessRule(rule);
            directory.SetAccessControl(security);

            // Windows propagates a new inheritable rule to children that still inherit,
            // but not to any that stopped. Every existing file gets it directly, which is
            // also what repairs a vault built by an earlier elevated run: putting a mod
            // away deletes the stored original, so read access is not enough.
            var flat = new System.Security.AccessControl.FileSystemAccessRule(
                user,
                System.Security.AccessControl.FileSystemRights.Modify,
                System.Security.AccessControl.AccessControlType.Allow);

            foreach (var path in Directory.EnumerateFiles(vaultRoot, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var file = new FileInfo(path);
                    var fileSecurity = file.GetAccessControl();
                    fileSecurity.AddAccessRule(flat);
                    file.SetAccessControl(fileSecurity);
                }
                catch
                {
                    // One stubborn file is not worth failing the install over.
                }
            }

            foreach (var path in Directory.EnumerateDirectories(vaultRoot, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var child = new DirectoryInfo(path);
                    var childSecurity = child.GetAccessControl();
                    childSecurity.AddAccessRule(rule);
                    child.SetAccessControl(childSecurity);
                }
                catch
                {
                }
            }
        }
        catch
        {
            // Not being able to widen the rules is not a reason to refuse the install;
            // the helper itself is elevated and can write regardless.
        }
    }



    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
