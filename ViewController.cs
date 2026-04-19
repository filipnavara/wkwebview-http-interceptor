using System.Runtime.Versioning;
using Foundation;
using ObjCRuntime;
using WebKit;

namespace wkwebviewtest;

[Register ("ViewController")]
public partial class ViewController : NSViewController
{
    private WKWebView? webView;

    protected ViewController(NativeHandle handle) : base(handle)
    {
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        SetupWebView();
    }

    private async void SetupWebView()
    {
        var config = new WKWebViewConfiguration();
        config.SetUrlSchemeHandler(new AppPageSchemeHandler(SampleHtml), "app-page");

		// Cascade of http(s) interception strategies, picked at runtime based on what
		// the current macOS version exposes and which setup path successfully
		// initializes.  In every case the goal is the same: see every sub-resource URL
		// (including CSS url() and sub-frame loads) with the originating frame, and
		// short-circuit the load in our process.
		//
		//   1. macOS 15.4+ — SetupExtension: a bundled WKWebExtension whose DNR rules
		//      redirect http/https → filter-http/filter-https via
		//      `redirect.regex-substitution`.  No private API.
		//   2. Older macOS where WKWebpagePreferences responds to
		//      `_setActiveContentRuleListActionPatterns:` — SetupContentRuleList: a
		//      WKContentRuleList with the undocumented `redirect` action doing the
		//      same rewrite.  Without that private SPI, redirect actions are silently
		//      suppressed by DocumentLoader::allowsActiveContentRuleListActionsForURL
		//      (block/notify actions still fire, but we need redirect).
		//   3. Last resort — WKWebViewSwizzle.Install: swizzle
		//      +[WKWebView handlesURLScheme:] so WKURLSchemeHandler can be registered
		//      directly against http/https.
        var interceptionConfigured = false;
        var filterSchemeHandlersRegistered = false;

        if (OperatingSystem.IsMacOSVersionAtLeast(15, 4))
        {
            RegisterFilterSchemeHandlers(config);
            filterSchemeHandlersRegistered = true;
            interceptionConfigured = await SetupExtension(config);
            if (!interceptionConfigured)
                Console.Error.WriteLine("[Setup] Falling back from web extension strategy.");
        }

        if (!interceptionConfigured &&
            config.DefaultWebpagePreferences!.RespondsToSelector(selSetActiveContentRuleListActionPatterns))
        {
            if (!filterSchemeHandlersRegistered)
            {
                RegisterFilterSchemeHandlers(config);
                filterSchemeHandlersRegistered = true;
            }

            interceptionConfigured = await SetupContentRuleList(config);
            if (!interceptionConfigured)
                Console.Error.WriteLine("[Setup] Falling back from content-rule-list strategy.");
        }

        if (!interceptionConfigured)
        {
            WKWebViewSwizzle.Install();
            config.SetUrlSchemeHandler(new InterceptingSchemeHandler(), "http");
            config.SetUrlSchemeHandler(new InterceptingSchemeHandler(), "https");
        }

        webView = new WKWebView(View.Bounds, config)
        {
            AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable
        };

        View.AddSubview(webView);

        webView.LoadRequest(new NSUrlRequest(new NSUrl("app-page://localhost/")));
    }

    // Loads the bundled Extension/ directory as a web extension and attaches its
    // controller to the config.  The extension's DNR rules rewrite http/https →
    // filter-http/filter-https via `regex-substitution`.
    //
    // Must run BEFORE the WKWebView is constructed: WKWebView snapshots its
    // WKWebViewConfiguration at init time, so `config.WebExtensionController`
    // assignments made after construction have no effect on an already-created webView.
    //
    // Manifest `host_permissions` and `permissions` entries are only a *request* —
    // match patterns and the `declarativeNetRequestWithHostAccess` permission must
    // be explicitly granted on the context, otherwise
    // DocumentLoader::allowsActiveContentRuleListActionsForURL suppresses every
    // redirect action the rules would produce. Returns false when setup fails so
    // the caller can continue down the strategy cascade.
    [SupportedOSPlatform("macos15.4")]
    async Task<bool> SetupExtension(WKWebViewConfiguration config)
    {
        Console.WriteLine("[ExtBridge] Loading web extension…");

        var dir = Path.Combine(NSBundle.MainBundle.ResourcePath!, "Extension");
        var tcs = new TaskCompletionSource<(WKWebExtension?, NSError?)>();
        WKWebExtension.Create(NSUrl.FromFilename(dir),
            (ext, err) => tcs.SetResult((ext, err)));
        var (extension, createErr) = await tcs.Task;
        if (extension is null)
        {
            Console.Error.WriteLine($"[ExtBridge] Load failed: {createErr}");
            return false;
        }

        var ctx = new WKWebExtensionContext(extension);
        foreach (NSString perm in extension.WeakRequestedPermissions)
            ctx.SetPermissionStatus(
                WKWebExtensionContextPermissionStatus.GrantedExplicitly, (string)perm);
        foreach (var pattern in extension.RequestedPermissionMatchPatterns.ToArray())
            ctx.SetPermissionStatus(
                WKWebExtensionContextPermissionStatus.GrantedExplicitly, pattern);

        var controller = new WKWebExtensionController();
        if (!controller.LoadExtensionContext(ctx, out var loadErr))
        {
            Console.Error.WriteLine($"[ExtBridge] LoadExtensionContext failed: {loadErr}");
            return false;
        }
        config.WebExtensionController = controller;
        Console.WriteLine("[ExtBridge] Web extension loaded.");
        return true;
    }

    // WKContentRuleList with the undocumented `redirect` action
    // (ContentExtensionParser.cpp:273-278), mirroring the DNR setup the Extension
    // strategy uses: `regex-substitution` rewrites http(s) → filter-http(s) while
    // preserving host/path/query.  Redirect is gated by
    // DocumentLoader::allowsActiveContentRuleListActionsForURL, which consults a
    // per-rule-list pattern map populated from WebsitePoliciesData.  For non-extension
    // rule lists that map is empty unless we populate it via the private
    // WKWebpagePreferences SPI `_setActiveContentRuleListActionPatterns:` — without
    // the SPI, `block`/`notify` actions still fire but every redirect is silently
    // suppressed.
    //
    // `regex-substitution` (not `transform.scheme`) is required: URL::setProtocol
    // silently no-ops across the WHATWG special↔non-special scheme boundary, so
    // swapping http → filter-http via `transform` leaves the URL unchanged.
    // Returns false when setup fails so the caller can continue down the strategy
    // cascade.
    async Task<bool> SetupContentRuleList(WKWebViewConfiguration config)
    {
        const string rulesJson = """
			[
			  {
			    "trigger": { "url-filter": "^http://(.*)" },
			    "action": {
			      "type": "redirect",
			      "redirect": { "regex-substitution": "filter-http://\\1" }
			    }
			  },
			  {
			    "trigger": { "url-filter": "^https://(.*)" },
			    "action": {
			      "type": "redirect",
			      "redirect": { "regex-substitution": "filter-https://\\1" }
			    }
			  }
			]
			""";

        try
        {
            var ruleList = await WKContentRuleListStore.DefaultStore
                .CompileContentRuleListAsync("BlockHttpHttps", rulesJson);
            config.UserContentController.AddContentRuleList(ruleList);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ContentRules] Compile failed: {ex.Message}");
            return false;
        }

        var actionPatterns = NSDictionary.FromObjectsAndKeys(
            new NSObject[] { NSArray.FromStrings("*://*/*") },
            new NSObject[] { (NSString)"BlockHttpHttps" });
        SetActiveContentRuleListActionPatterns(
            config.DefaultWebpagePreferences!, actionPatterns);
        return true;
    }

    private const string SampleHtml = """
		<!DOCTYPE html>
		<html>
		<head>
		    <meta charset="utf-8">
		    <title>WKWebView Content Blocker Demo</title>
		    <link rel="stylesheet" href="http://fonts.example.com/style.css">
		    <style>
		        body { font-family: -apple-system, sans-serif; padding: 24px; background: #f5f5f5; }
		        h1 { color: #333; }
		        .section { background: white; border-radius: 8px; padding: 16px; margin: 16px 0;
		                   box-shadow: 0 1px 3px rgba(0,0,0,.1); }
		        .images { display: flex; flex-wrap: wrap; gap: 12px; margin-top: 12px; }
		        figure { margin: 0; text-align: center; }
		        img { width: 120px; height: 80px; object-fit: cover; border: 2px solid #ddd;
		              border-radius: 4px; display: block; background: #eee; }
		        figcaption { font-size: 11px; color: #666; margin-top: 4px;
		                     word-break: break-all; max-width: 120px; }
		        .bg-box { width: 120px; height: 80px; border: 2px solid #ddd; border-radius: 4px;
		                  background-color: #eee; background-size: cover;
		                  font-size: 10px; color: #999; padding: 4px; word-break: break-all; }
		        p { color: #555; line-height: 1.5; }
		        code { background: #eee; padding: 1px 4px; border-radius: 3px; }
		    </style>
		</head>
		<body>
		    <h1>Content Blocker Demo</h1>
		    <p>All <code>http://</code> and <code>https://</code> sub-resource requests
		       are intercepted by the active strategy (WKWebExtension DNR, WKContentRuleList
		       redirect, or the <code>+[WKWebView handlesURLScheme:]</code> swizzle) and
		       logged with their originating frame URL.</p>

		    <div class="section">
		        <h2>HTTP images</h2>
		        <div class="images">
		            <figure>
		                <img src="http://www.example.com/photo1.jpg" alt="HTTP 1">
		                <figcaption>http://www.example.com/photo1.jpg</figcaption>
		            </figure>
		            <figure>
		                <img src="http://images.example.org/banner.png" alt="HTTP 2">
		                <figcaption>http://images.example.org/banner.png</figcaption>
		            </figure>
		        </div>
		    </div>

		    <div class="section">
		        <h2>HTTPS images</h2>
		        <div class="images">
		            <figure>
		                <img src="https://www.example.com/photo1.jpg" alt="HTTPS 1">
		                <figcaption>https://www.example.com/photo1.jpg</figcaption>
		            </figure>
		            <figure>
		                <img src="https://images.example.org/banner.png" alt="HTTPS 2">
		                <figcaption>https://images.example.org/banner.png</figcaption>
		            </figure>
		        </div>
		    </div>

		    <div class="section">
		        <h2>Inline CSS <code>background-image: url()</code></h2>
		        <div class="images">
		            <figure>
		                <div class="bg-box"
		                     style="background-image: url('http://www.example.com/bg-photo.jpg')">
		                    http://…/bg-photo.jpg
		                </div>
		                <figcaption>http://…/bg-photo.jpg</figcaption>
		            </figure>
		            <figure>
		                <div class="bg-box"
		                     style="background-image: url('https://www.example.com/bg-photo.jpg')">
		                    https://…/bg-photo.jpg
		                </div>
		                <figcaption>https://…/bg-photo.jpg</figcaption>
		            </figure>
		        </div>
		    </div>

		    <iframe src="app-page://subframe/" style="width:100%;border:none;margin-top:16px"></iframe>
		    <script src="https://cdn.example.com/analytics.js"></script>
		</body>
		</html>
		""";

    public override NSObject RepresentedObject
    {
        get => base.RepresentedObject;
        set => base.RepresentedObject = value;
    }

    // ── Private WKWebpagePreferences SPI: _setActiveContentRuleListActionPatterns: ──

    static readonly Selector selSetActiveContentRuleListActionPatterns =
        new("_setActiveContentRuleListActionPatterns:");

    static void RegisterFilterSchemeHandlers(WKWebViewConfiguration config)
    {
        config.SetUrlSchemeHandler(new InterceptingSchemeHandler(), "filter-http");
        config.SetUrlSchemeHandler(new InterceptingSchemeHandler(), "filter-https");
    }

    [System.Runtime.InteropServices.DllImport(
        "/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    static extern void void_objc_msgSend_IntPtr(
        NativeHandle receiver, IntPtr selector, NativeHandle arg1);

    static void SetActiveContentRuleListActionPatterns(
        WKWebpagePreferences prefs, NSDictionary patterns)
        => void_objc_msgSend_IntPtr(
            prefs.Handle, selSetActiveContentRuleListActionPatterns.Handle, patterns.Handle);
}
