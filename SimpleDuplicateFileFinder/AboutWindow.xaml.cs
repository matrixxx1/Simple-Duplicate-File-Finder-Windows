using System.Windows;

namespace SimpleDuplicateFileFinder;

public partial class AboutWindow : Window
{
    public AboutWindow(bool isTrialMode, int trialDaysLeft, bool isTrialExpired, string appVersion)
    {
        InitializeComponent();
        Title = $"About - Simple Duplicate File Finder {appVersion}";
        TrialStatusText.Text = isTrialExpired
            ? "License state: Trial Expired. Cleanup actions are locked until a full key is applied."
            : isTrialMode
                ? $"License state: Trial active, {trialDaysLeft} day(s) remaining."
                : "License state: Full version unlocked.";
    }
}
