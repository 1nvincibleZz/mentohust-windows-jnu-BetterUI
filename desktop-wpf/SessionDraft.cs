using System.Collections.ObjectModel;

namespace MentoHUST.Desktop
{
    public sealed class AccountDraft
    {
        public string SourceSection { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string Ip { get; set; }
        public string OriginalPassword { get; set; }
        public string OriginalEncodedPassword { get; set; }
        public bool PasswordReadable = true;
        public override string ToString() { return Username; }
        public AccountDraft Clone()
        {
            return new AccountDraft { SourceSection = SourceSection, Username = Username, Password = Password,
                Ip = Ip, OriginalPassword = OriginalPassword, OriginalEncodedPassword = OriginalEncodedPassword,
                PasswordReadable = PasswordReadable };
        }
    }

    public sealed class SessionDraft
    {
        public ObservableCollection<AccountDraft> Accounts = new ObservableCollection<AccountDraft>();
        public bool AutoRun, AutoAuthenticate, AutoMinimize, BindMac, UsePackage, HotspotAfterSuccess;
        public int Multicast = 1, Dhcp, Timeout = 8, EchoTime = 20, Reconnect;
        public string PackagePath = "", DefaultAccount = "", DefaultAdapter = "";

        public SessionDraft Clone()
        {
            var copy = new SessionDraft {
                AutoRun = AutoRun, AutoAuthenticate = AutoAuthenticate, AutoMinimize = AutoMinimize,
                BindMac = BindMac, UsePackage = UsePackage, HotspotAfterSuccess = HotspotAfterSuccess, Multicast = Multicast, Dhcp = Dhcp,
                Timeout = Timeout, EchoTime = EchoTime, Reconnect = Reconnect,
                PackagePath = PackagePath, DefaultAccount = DefaultAccount, DefaultAdapter = DefaultAdapter
            };
            foreach (var account in Accounts) copy.Accounts.Add(account.Clone());
            return copy;
        }

        public static SessionDraft CreatePreview()
        {
            var draft = new SessionDraft();
            draft.Accounts.Add(new AccountDraft { Username = "demo_student", Password = "preview_only", Ip = "0.0.0.0" });
            draft.Accounts.Add(new AccountDraft { Username = "test_account", Password = "preview_only", Ip = "0.0.0.0" });
            return draft;
        }
    }

    public sealed class AdapterInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool CaptureAvailable { get; set; }
        public override string ToString() { return Name; }
    }
}
