#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views.InputMethods;
using Android.Views;
using Android.Widget;

[Activity(Label = "Connect to PC", Exported = false, WindowSoftInputMode = SoftInput.StateAlwaysHidden)]
public sealed class OnlineActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var settings = GetPreferences(FileCreationMode.Private)!;
        int padding = (int)((Resources?.DisplayMetrics?.Density ?? 1) * 20);
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical, FocusableInTouchMode = true };
        layout.SetPadding(padding, padding, padding, padding);
        var heading = new TextView(this) { Text = "CFL Engineering Workbench", TextSize = 25 };
        var address = new EditText(this)
        {
            Hint = "HTTPS workbench address",
            Text = settings.GetString("workbenchUrl", ""),
            InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri,
            ImeOptions = ImeAction.Go
        };
        address.SetSingleLine();
        var help = new TextView(this) { Text = WorkbenchConnection.SetupHint, TextSize = 15 };
        var open = new Button(this) { Text = "Open workbench" };
        var status = new TextView(this) { AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        open.Click += async (_, _) =>
        {
            var error = WorkbenchConnection.Parse(address.Text, out var url);
            if (url == null)
            {
                status.Text = error;
                return;
            }
            if (!open.Enabled) return;
            open.Enabled = address.Enabled = false;
            status.Text = WorkbenchConnection.Checking;
            (GetSystemService(InputMethodService) as InputMethodManager)?.HideSoftInputFromWindow(address.WindowToken, HideSoftInputFlags.None);
            try
            {
                var (reachable, message) = await WorkbenchConnection.CheckAsync(url);
                if (IsDestroyed || IsFinishing) return;
                status.Text = message;
                if (!reachable) return;
                StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(url.AbsoluteUri)));
                settings.Edit()?.PutString("workbenchUrl", url.AbsoluteUri)?.Apply();
            }
            catch (ActivityNotFoundException) { status.Text = "No browser is installed."; }
            finally { if (!IsDestroyed) open.Enabled = address.Enabled = true; }
        };
        address.EditorAction += (_, args) =>
        {
            if (args.ActionId != ImeAction.Go) return;
            args.Handled = true;
            if (open.Enabled) open.PerformClick();
        };
        layout.AddView(heading);
        layout.AddView(help);
        layout.AddView(address, ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        layout.AddView(open);
        layout.AddView(status);
        var scroll = new ScrollView(this);
        scroll.AddView(layout);
        SetContentView(scroll);
        layout.RequestFocus();
    }
}
#elif IOS
using Foundation;
using UIKit;

public static class MobileProgram
{
    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        var controller = new UIViewController();
        controller.View!.BackgroundColor = UIColor.SystemBackground;
        var heading = new UILabel { Text = "CFL Engineering Workbench", Font = UIFont.BoldSystemFontOfSize(24), Lines = 0 };
        var address = new UITextField
        {
            Placeholder = "HTTPS workbench address", Text = NSUserDefaults.StandardUserDefaults.StringForKey("workbenchUrl"),
            BorderStyle = UITextBorderStyle.RoundedRect, KeyboardType = UIKeyboardType.Url,
            AutocorrectionType = UITextAutocorrectionType.No, AutocapitalizationType = UITextAutocapitalizationType.None,
            ReturnKeyType = UIReturnKeyType.Go
        };
        var help = new UILabel { Text = WorkbenchConnection.SetupHint, Lines = 0, Font = UIFont.SystemFontOfSize(15) };
        var status = new UILabel { Lines = 0 };
        var open = UIButton.FromType(UIButtonType.System);
        open.SetTitle("Open workbench", UIControlState.Normal);
        async void OpenWorkbench()
        {
            var error = WorkbenchConnection.Parse(address.Text, out var url);
            if (url == null)
            {
                status.Text = error;
                return;
            }
            if (!open.Enabled) return;
            address.ResignFirstResponder();
            open.Enabled = address.Enabled = false;
            status.Text = WorkbenchConnection.Checking;
            try
            {
                var (reachable, message) = await WorkbenchConnection.CheckAsync(url);
                status.Text = message;
                if (!reachable) return;
                application.OpenUrl(new NSUrl(url.AbsoluteUri), new UIApplicationOpenUrlOptions(), completed =>
                {
                    if (completed) NSUserDefaults.StandardUserDefaults.SetString(url.AbsoluteUri, "workbenchUrl");
                    else status.Text = "Unable to open the workbench in Safari.";
                });
            }
            finally { open.Enabled = address.Enabled = true; }
        }
        open.TouchUpInside += (_, _) => OpenWorkbench();
        address.ShouldReturn = _ =>
        {
            OpenWorkbench();
            return true;
        };
        var stack = new UIStackView(new UIView[] { heading, help, address, open, status })
        {
            Axis = UILayoutConstraintAxis.Vertical, Spacing = 12, TranslatesAutoresizingMaskIntoConstraints = false
        };
        controller.View.AddSubview(stack);
        var safe = controller.View.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            stack.TopAnchor.ConstraintEqualTo(safe.TopAnchor, 22),
            stack.LeadingAnchor.ConstraintEqualTo(safe.LeadingAnchor, 20),
            stack.TrailingAnchor.ConstraintEqualTo(safe.TrailingAnchor, -20)
        });
        Window = new UIWindow(UIScreen.MainScreen.Bounds) { RootViewController = controller };
        Window.MakeKeyAndVisible();
        return true;
    }
}
#endif
