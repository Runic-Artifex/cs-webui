using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CsWebUi.Native;

/// <summary>
/// Direct, allocation-free bindings for the WebUI 2.5.0-beta.4 C ABI.
/// Pointers returned by this class are owned by WebUI unless the corresponding
/// upstream API explicitly documents that <see cref="Free"/> must be called.
/// </summary>
public static unsafe partial class WebUiNative
{
    static WebUiNative() => WebUiNativeLibrary.EnsureResolverInstalled();

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_new_window")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint NewWindow();

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_new_window_id")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint NewWindowId(nuint windowNumber);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_new_window_id")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint GetNewWindowId();

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_bind")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint Bind(nuint window, byte* element, delegate* unmanaged[Cdecl]<WebUiEventNative*, void> callback);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_context")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetContext(nuint window, byte* element, void* context);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_context")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void* GetContext(WebUiEventNative* webUiEvent);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_best_browser")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint GetBestBrowser(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_show")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte Show(nuint window, byte* content);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_show_client")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte ShowClient(WebUiEventNative* webUiEvent, byte* content);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_show_browser")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte ShowBrowser(nuint window, byte* content, nuint browser);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_start_server")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* StartServer(nuint window, byte* content);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_show_wv")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte ShowWebView(nuint window, byte* content);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_kiosk")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetKiosk(nuint window, byte status);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_focus")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Focus(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_custom_parameters")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetCustomParameters(nuint window, byte* parameters);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_high_contrast")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetHighContrast(nuint window, byte status);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_resizable")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetResizable(nuint window, byte status);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_is_high_contrast")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte IsHighContrast();

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_browser_exist")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte BrowserExist(nuint browser);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_wait")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Wait();

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_wait_async")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte WaitAsync();

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Close(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_minimize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Minimize(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_maximize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Maximize(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_close_client")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void CloseClient(WebUiEventNative* webUiEvent);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Destroy(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_exit")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Exit();

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_root_folder")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte SetRootFolder(nuint window, byte* path);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_browser_folder")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetBrowserFolder(byte* path);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_default_root_folder")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte SetDefaultRootFolder(byte* path);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_close_handler_wv")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetCloseHandlerWebView(nuint window, delegate* unmanaged[Cdecl]<nuint, byte> handler);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_file_handler")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetFileHandler(nuint window, delegate* unmanaged[Cdecl]<byte*, int*, void*> handler);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_file_handler_window")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetFileHandlerWindow(nuint window, delegate* unmanaged[Cdecl]<nuint, byte*, int*, void*> handler);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_set_response_file_handler")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void InterfaceSetResponseFileHandler(nuint window, void* response, int length);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_is_shown")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte IsShown(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_timeout")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetTimeout(nuint seconds);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_icon")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetIcon(nuint window, byte* icon, byte* iconType);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_icon_file")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetIconFile(nuint window, byte* path);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_encode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* Encode(byte* value);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_decode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* Decode(byte* value);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Free(void* pointer);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_malloc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void* Malloc(nuint size);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_memcpy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Memcpy(void* destination, void* source, nuint count);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_send_raw")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SendRaw(nuint window, byte* function, void* raw, nuint size);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_send_raw_client")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SendRawClient(WebUiEventNative* webUiEvent, byte* function, void* raw, nuint size);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_hide")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetHide(nuint window, byte status);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetSize(nuint window, uint width, uint height);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_minimum_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetMinimumSize(nuint window, uint width, uint height);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_position")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetPosition(nuint window, uint x, uint y);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_center")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetCenter(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_profile")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetProfile(nuint window, byte* name, byte* path);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_proxy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetProxy(nuint window, byte* proxyServer);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_url")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* GetUrl(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_open_url")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void OpenUrl(byte* url);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_public")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetPublic(nuint window, byte status);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_navigate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Navigate(nuint window, byte* url);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_navigate_client")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void NavigateClient(WebUiEventNative* webUiEvent, byte* url);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_clean")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Clean();

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_delete_all_profiles")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void DeleteAllProfiles();

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_delete_profile")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void DeleteProfile(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_parent_process_id")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint GetParentProcessId(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_child_process_id")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint GetChildProcessId(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_win32_get_hwnd")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void* Win32GetHwnd(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_hwnd")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void* GetHwnd(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_port")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint GetPort(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_port")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte SetPort(nuint window, nuint port);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_free_port")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint GetFreePort();

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_logger")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetLogger(delegate* unmanaged[Cdecl]<nuint, byte*, void*, void> callback, void* userData);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_config")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetConfig(WebUiConfig option, byte status);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_event_blocking")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetEventBlocking(nuint window, byte status);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_frameless")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetFrameless(nuint window, byte status);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_transparent")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetTransparent(nuint window, byte status);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_mime_type")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* GetMimeType(byte* file);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_tls_certificate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte SetTlsCertificate(byte* certificatePem, byte* privateKeyPem);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_run")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Run(nuint window, byte* script);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_run_client")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void RunClient(WebUiEventNative* webUiEvent, byte* script);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_script")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte Script(nuint window, byte* script, nuint timeout, byte* buffer, nuint bufferLength);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_script_client")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte ScriptClient(WebUiEventNative* webUiEvent, byte* script, nuint timeout, byte* buffer, nuint bufferLength);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_set_runtime")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void SetRuntime(nuint window, nuint runtime);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint GetCount(WebUiEventNative* webUiEvent);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_int_at")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial long GetIntAt(WebUiEventNative* webUiEvent, nuint index);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_int")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial long GetInt(WebUiEventNative* webUiEvent);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_float_at")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial double GetFloatAt(WebUiEventNative* webUiEvent, nuint index);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_float")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial double GetFloat(WebUiEventNative* webUiEvent);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_string_at")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* GetStringAt(WebUiEventNative* webUiEvent, nuint index);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_string")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* GetString(WebUiEventNative* webUiEvent);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_bool_at")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte GetBoolAt(WebUiEventNative* webUiEvent, nuint index);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_bool")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte GetBool(WebUiEventNative* webUiEvent);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_size_at")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint GetSizeAt(WebUiEventNative* webUiEvent, nuint index);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint GetSize(WebUiEventNative* webUiEvent);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_return_int")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void ReturnInt(WebUiEventNative* webUiEvent, long value);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_return_float")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void ReturnFloat(WebUiEventNative* webUiEvent, double value);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_return_string")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void ReturnString(WebUiEventNative* webUiEvent, byte* value);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_return_bool")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void ReturnBool(WebUiEventNative* webUiEvent, byte value);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_return_http")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void ReturnHttp(nuint window, void* response, int length);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_last_error_number")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint GetLastErrorNumber();

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_get_last_error_message")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* GetLastErrorMessage();

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_bind")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint InterfaceBind(nuint window, byte* element, delegate* unmanaged[Cdecl]<nuint, nuint, byte*, nuint, nuint, void> callback);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_set_response")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void InterfaceSetResponse(nuint window, nuint eventNumber, byte* response);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_is_app_running")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte InterfaceIsAppRunning();

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_get_window_id")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint InterfaceGetWindowId(nuint window);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_get_string_at")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* InterfaceGetStringAt(nuint window, nuint eventNumber, nuint index);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_get_int_at")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial long InterfaceGetIntAt(nuint window, nuint eventNumber, nuint index);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_get_float_at")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial double InterfaceGetFloatAt(nuint window, nuint eventNumber, nuint index);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_get_bool_at")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte InterfaceGetBoolAt(nuint window, nuint eventNumber, nuint index);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_get_size_at")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint InterfaceGetSizeAt(nuint window, nuint eventNumber, nuint index);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_show_client")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte InterfaceShowClient(nuint window, nuint eventNumber, byte* content);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_close_client")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void InterfaceCloseClient(nuint window, nuint eventNumber);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_send_raw_client")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void InterfaceSendRawClient(nuint window, nuint eventNumber, byte* function, void* raw, nuint size);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_navigate_client")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void InterfaceNavigateClient(nuint window, nuint eventNumber, byte* url);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_run_client")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void InterfaceRunClient(nuint window, nuint eventNumber, byte* script);

    [LibraryImport(WebUiNativeLibrary.LibraryName, EntryPoint = "webui_interface_script_client")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte InterfaceScriptClient(nuint window, nuint eventNumber, byte* script, nuint timeout, byte* buffer, nuint bufferLength);
}
