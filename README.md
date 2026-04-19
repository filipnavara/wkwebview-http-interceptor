# wkwebview-http-interceptor

A macOS sample app that demonstrates three ways to intercept every `http(s)` sub-resource request a `WKWebView` makes — image, stylesheet, script, CSS `url()`, sub-frame, etc. — recover the originating frame URL, and short-circuit the load in-process.

Each strategy ends up routing traffic through the same `InterceptingSchemeHandler`, which logs the original URL and the frame that triggered it (via the private `WKURLSchemeTask._frame` selector), then cancels the load with `NSURLErrorCancelled`.

## Strategies

The active strategy is picked at runtime by `ViewController.SetupWebView`, based on what the current macOS version exposes. Most to least preferred:

### 1. WKWebExtension (macOS 15.4+) — no private API

A bundled MV3 web extension (`Extension/manifest.json`, `Extension/rules.json`) declares `declarativeNetRequest` rules that rewrite every `http(s)` URL into `filter-http(s)` via `redirect.regex-substitution`. `WKWebViewConfiguration.SetUrlSchemeHandler` is registered for `filter-http` / `filter-https`, so the redirected load lands in our handler with the original URL trivially recoverable (strip the `filter-` prefix).

Caveats:

- `redirect.transform.scheme` doesn't work for `http → filter-http`: `URL::setProtocol` silently no-ops across the WHATWG special↔non-special scheme boundary. `regex-substitution` constructs a fresh URL and sidesteps the check.
- `host_permissions` and `permissions` in the manifest are only *requests*. The host must explicitly grant them on the `WKWebExtensionContext` (`SetupExtension` does this in a loop over `WeakRequestedPermissions` and `RequestedPermissionMatchPatterns`) — otherwise `DocumentLoader::allowsActiveContentRuleListActionsForURL` suppresses every redirect.
- The `WKWebExtensionController` must be assigned to the config *before* the `WKWebView` is constructed; the config is snapshotted at init time.

### 2. WKContentRuleList redirect — undocumented but supported

Uses the **undocumented** `{"type": "redirect", "redirect": {...}}` action in `WKContentRuleList` JSON, with the same `regex-substitution` rewrite. Parsed by `WebKit/Source/WebCore/contentextensions/ContentExtensionParser.cpp:273-278`.

Redirect actions are gated by `DocumentLoader::allowsActiveContentRuleListActionsForURL`, which consults a per-rule-list pattern map populated from `WebsitePoliciesData`. For non-extension rule lists that map is empty unless we populate it via the **private** `WKWebpagePreferences` SPI `_setActiveContentRuleListActionPatterns:`. Without it, `block` and `notify` actions still fire but every redirect is silently dropped.

The strategy is chosen only if a `WKWebpagePreferences` instance responds to that selector at runtime.

### 3. `+[WKWebView handlesURLScheme:]` swizzle — last resort

`WKWebViewSwizzle.Install()` swizzles the class method so it reports `false` for `http` / `https`. That fools `SetUrlSchemeHandler` into accepting those schemes (it otherwise throws `NSInvalidArgumentException`). The same `InterceptingSchemeHandler` is then registered directly against `http` / `https`.

Caveats:

- Process-wide and permanent for the lifetime of the process.
- Relies on ObjC runtime manipulation.

## Files

| File | Purpose |
|------|---------|
| `ViewController.cs` | Strategy selection + the Extension/ContentRuleList setup routines. |
| `BlockedSchemeHandler.cs` | `InterceptingSchemeHandler` (used by all three strategies) and `AppPageSchemeHandler` (serves the demo HTML from `app-page://`). |
| `WKWebViewSwizzle.cs` | The `+[WKWebView handlesURLScheme:]` class-method swizzle. |
| `Extension/manifest.json` | MV3 manifest declaring `declarativeNetRequest` + host permissions. |
| `Extension/rules.json` | DNR redirect rules for `http → filter-http` and `https → filter-https`. |

## Building & running

```
dotnet build
dotnet run
```

Requires .NET 10 with the `net10.0-macos` workload. Output looks like:

```
[Intercept] http://www.example.com/photo1.jpg
            frame=app-page://localhost/  isMainFrame=True
[Intercept] http://subframe.example.com/asset.png
            frame=app-page://subframe/   isMainFrame=False
```

## Notes on the private API surface

Two selectors are used via `objc_msgSend`:

- `WKURLSchemeTask._frame` — returns `WKFrameInfo` for the originating frame. Used by `InterceptingSchemeHandler`.
- `WKWebpagePreferences._setActiveContentRuleListActionPatterns:` — populates the action-pattern map that gates `WKContentRuleList` redirect actions. Used by the ContentRuleList strategy and probed at runtime to decide whether that strategy is available.

Both may be renamed or removed in future WebKit releases.
