using System;
using System.Threading;
using System.Threading.Tasks;

namespace MentoHUST.Desktop
{
    // UI-to-engine boundary. No WPF types, window handles or control IDs here.
    public enum AuthenticationState { Disconnected, Starting, Identity, Challenge, Dhcp, Connected, Failed }

    public sealed class AuthenticationRequest
    {
        public string AccountKey { get; set; }
        public string AdapterKey { get; set; }
    }

    public sealed class AuthenticationEvent : EventArgs
    {
        public AuthenticationState State { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; }
        public long Sequence { get; set; }
    }

    public interface IAuthenticationClient
    {
        bool IsAvailable { get; }
        event EventHandler<AuthenticationEvent> StateChanged;
        Task StartAsync(AuthenticationRequest request, CancellationToken cancellation);
        Task StopAsync(CancellationToken cancellation);
    }

    // Preview cannot authenticate. In particular, never simulate a successful connection.
    public sealed class UnavailableAuthenticationClient : IAuthenticationClient
    {
        public bool IsAvailable { get { return false; } }
        public event EventHandler<AuthenticationEvent> StateChanged { add { } remove { } }
        public Task StartAsync(AuthenticationRequest request, CancellationToken cancellation)
        {
            throw new NotSupportedException("认证引擎尚未接入。");
        }
        public Task StopAsync(CancellationToken cancellation)
        {
            throw new NotSupportedException("认证引擎尚未接入。");
        }
    }
}
