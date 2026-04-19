using System.Runtime.InteropServices;
using ObjCRuntime;

namespace wkwebviewtest;

/// <summary>
/// Swizzles +[WKWebView handlesURLScheme:] so that it returns false for "http"
/// and "https".  That fools WKWebViewConfiguration.SetUrlSchemeHandler into
/// accepting those schemes, which it otherwise refuses with NSInvalidArgumentException
/// ("'https' is a URL scheme that WKWebView handles natively").
///
/// Must be called before the first SetUrlSchemeHandler("http"/"https") call.
/// The swizzle is process-wide and permanent for the lifetime of the process.
/// </summary>
static class WKWebViewSwizzle
{
    // Signature of a WKWebView class-method IMP:
    //   self  = the WKWebView Class object
    //   sel   = the selector (_cmd)
    //   scheme = NSString* urlScheme argument
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    delegate bool HandlesURLSchemeFn(IntPtr self, IntPtr sel, IntPtr scheme);

    // Strong references — must outlive the swizzle to prevent GC collection.
    static HandlesURLSchemeFn? s_original;
    static HandlesURLSchemeFn? s_replacement;

    [DllImport("libobjc.dylib")]
    static extern IntPtr class_getClassMethod(IntPtr cls, IntPtr sel);

    [DllImport("libobjc.dylib")]
    static extern IntPtr method_getImplementation(IntPtr method);

    [DllImport("libobjc.dylib")]
    static extern void method_setImplementation(IntPtr method, IntPtr imp);

    public static void Install()
    {
        var cls = Class.GetHandle("WKWebView");
        var sel = Selector.GetHandle("handlesURLScheme:");
        var method = class_getClassMethod(cls, sel);

        s_original = Marshal.GetDelegateForFunctionPointer<HandlesURLSchemeFn>(
            method_getImplementation(method));
        s_replacement = Replacement;

        method_setImplementation(method,
            Marshal.GetFunctionPointerForDelegate(s_replacement));
    }

    static bool Replacement(IntPtr self, IntPtr sel, IntPtr schemeHandle)
    {
        var scheme = NSString.FromHandle(schemeHandle)?.ToString()?.ToLowerInvariant();
        if (scheme is "http" or "https")
            return false;
        return s_original!(self, sel, schemeHandle);
    }
}
