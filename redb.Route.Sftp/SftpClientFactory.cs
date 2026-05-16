using Renci.SshNet;

namespace redb.Route.Sftp;

/// <summary>
/// Factory for creating configured <see cref="SftpClient"/> instances from endpoint options.
/// Handles authentication methods (password, public key, keyboard-interactive), proxy tunneling,
/// host key verification, timeouts, keep-alive, and buffer sizes.
/// </summary>
internal static class SftpClientFactory
{
    /// <summary>
    /// Creates and configures an <see cref="SftpClient"/> ready for connecting.
    /// The caller is responsible for connecting, disconnecting, and disposing the client.
    /// </summary>
    public static SftpClient Create(SftpEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var authMethods = BuildAuthenticationMethods(options);
        if (authMethods.Length == 0)
            throw new InvalidOperationException(
                "No authentication method configured. Provide Password or PrivateKeyPath.");

        var connInfo = BuildConnectionInfo(options, authMethods);
        var client = new SftpClient(connInfo);

        client.OperationTimeout = TimeSpan.FromMilliseconds(options.OperationTimeout);
        client.BufferSize = (uint)options.BufferSize;

        if (options.KeepAliveInterval > 0)
        {
            client.KeepAliveInterval = TimeSpan.FromMilliseconds(options.KeepAliveInterval);
        }

        ConfigureHostKeyValidation(client, options);

        return client;
    }

    private static AuthenticationMethod[] BuildAuthenticationMethods(SftpEndpointOptions options)
    {
        var methods = new List<AuthenticationMethod>();

        // Public key authentication
        if (!string.IsNullOrEmpty(options.PrivateKeyPath))
        {
            var keyFile = string.IsNullOrEmpty(options.PrivateKeyPassphrase)
                ? new PrivateKeyFile(options.PrivateKeyPath)
                : new PrivateKeyFile(options.PrivateKeyPath, options.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(options.Username, keyFile));
        }

        // Password authentication
        if (!string.IsNullOrEmpty(options.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(options.Username, options.Password));
        }

        // Keyboard-interactive (2FA, PAM prompts)
        if (options.UseKeyboardInteractive)
        {
            var kia = new KeyboardInteractiveAuthenticationMethod(options.Username);
            var password = options.Password ?? "";
            kia.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = password;
                }
            };
            methods.Add(kia);
        }

        return methods.ToArray();
    }

    private static ConnectionInfo BuildConnectionInfo(SftpEndpointOptions options, AuthenticationMethod[] authMethods)
    {
        ConnectionInfo connInfo;

        if (options.ProxyType != SftpProxyType.None && !string.IsNullOrEmpty(options.ProxyHost))
        {
            var proxyType = options.ProxyType switch
            {
                SftpProxyType.Socks4 => ProxyTypes.Socks4,
                SftpProxyType.Socks5 => ProxyTypes.Socks5,
                SftpProxyType.Http => ProxyTypes.Http,
                _ => ProxyTypes.None
            };
            connInfo = new ConnectionInfo(
                options.Host, options.Port, options.Username,
                proxyType, options.ProxyHost, options.ProxyPort,
                options.ProxyUsername, options.ProxyPassword,
                authMethods);
        }
        else
        {
            connInfo = new ConnectionInfo(options.Host, options.Port, options.Username, authMethods);
        }

        connInfo.Timeout = TimeSpan.FromMilliseconds(options.ConnectionTimeout);

        return connInfo;
    }

    private static void ConfigureHostKeyValidation(SftpClient client, SftpEndpointOptions options)
    {
        if (!options.StrictHostKeyChecking)
        {
            // Accept all host keys (common in dev/test environments)
            client.HostKeyReceived += (_, e) => e.CanTrust = true;
        }
        else if (!string.IsNullOrEmpty(options.ServerFingerprint))
        {
            // Validate against a known fingerprint
            var expected = options.ServerFingerprint.Replace(":", "").Replace("-", "");
            client.HostKeyReceived += (_, e) =>
            {
                var actual = BitConverter.ToString(e.FingerPrint).Replace("-", "");
                e.CanTrust = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            };
        }
        // If StrictHostKeyChecking=true but no fingerprint/known_hosts, SSH.NET's default behavior applies
        // (may reject unknown hosts depending on version)
    }
}
