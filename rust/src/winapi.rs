// Pedaços do Windows usados pelas páginas: área de transferência, CPU/memória, saída de áudio, abrir e teclas.
use windows::core::{HSTRING, PCWSTR};
use windows::Win32::Foundation::{FILETIME, HANDLE, HGLOBAL, HWND};
use windows::Win32::System::DataExchange::{
    CloseClipboard, EmptyClipboard, GetClipboardData, GetClipboardSequenceNumber, IsClipboardFormatAvailable, OpenClipboard,
    RegisterClipboardFormatW, SetClipboardData,
};
use windows::Win32::System::Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE};
use windows::Win32::UI::Input::KeyboardAndMouse::{keybd_event, KEYBD_EVENT_FLAGS, KEYEVENTF_KEYUP};
use windows::Win32::UI::Shell::ShellExecuteW;
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

const CF_UNICODETEXT: u32 = 13;

pub fn clipboard_seq() -> u32 { unsafe { GetClipboardSequenceNumber() } }

/// Texto copiado agora (None se não for texto, se estiver ocupado ou se um gerenciador de senha pediu sigilo).
pub fn clipboard_text() -> Option<String> {
    unsafe {
        // Gerenciadores de senha marcam a cópia para ficar fora de históricos: respeita.
        let secret = RegisterClipboardFormatW(&HSTRING::from("ExcludeClipboardContentFromMonitorProcessing"));
        if secret != 0 && IsClipboardFormatAvailable(secret).is_ok() { return None; }
        if IsClipboardFormatAvailable(CF_UNICODETEXT).is_err() { return None; }
        OpenClipboard(None).ok()?;
        let text = (|| {
            let h = GetClipboardData(CF_UNICODETEXT).ok()?;
            let p = GlobalLock(HGLOBAL(h.0)) as *const u16;
            if p.is_null() { return None; }
            let mut len = 0;
            while *p.add(len) != 0 && len < 1_000_000 { len += 1; }
            let s = String::from_utf16_lossy(std::slice::from_raw_parts(p, len));
            let _ = GlobalUnlock(HGLOBAL(h.0));
            Some(s)
        })();
        let _ = CloseClipboard();
        text
    }
}

pub fn set_clipboard_text(text: &str) -> bool {
    unsafe {
        let wide: Vec<u16> = text.encode_utf16().chain(std::iter::once(0)).collect();
        if OpenClipboard(None).is_err() { return false; }
        let ok = (|| {
            EmptyClipboard().ok()?;
            let mem = GlobalAlloc(GMEM_MOVEABLE, wide.len() * 2).ok()?;
            let p = GlobalLock(mem) as *mut u16;
            if p.is_null() { return None; }
            std::ptr::copy_nonoverlapping(wide.as_ptr(), p, wide.len());
            let _ = GlobalUnlock(mem);
            SetClipboardData(CF_UNICODETEXT, Some(HANDLE(mem.0))).ok()?;
            Some(())
        })()
        .is_some();
        let _ = CloseClipboard();
        ok
    }
}

/// Uso da CPU (0–1) entre duas leituras.
pub struct Cpu { last: Option<(u64, u64)> }
impl Cpu {
    pub fn new() -> Self { Cpu { last: None } }
    pub fn read(&mut self) -> Option<f32> {
        let (mut idle, mut kernel, mut user) = (FILETIME::default(), FILETIME::default(), FILETIME::default());
        unsafe { windows::Win32::System::Threading::GetSystemTimes(Some(&mut idle), Some(&mut kernel), Some(&mut user)).ok()? };
        let v = |f: FILETIME| ((f.dwHighDateTime as u64) << 32) | f.dwLowDateTime as u64;
        let (idle, total) = (v(idle), v(kernel) + v(user)); // o tempo de kernel já inclui o ocioso
        let result = self.last.and_then(|(li, lt)| (total > lt).then(|| 1.0 - (idle - li) as f32 / (total - lt) as f32));
        self.last = Some((idle, total));
        result.map(|x| x.clamp(0.0, 1.0))
    }
}

/// (uso 0–1, usado GB, total GB)
pub fn memory() -> Option<(f32, f64, f64)> {
    use windows::Win32::System::SystemInformation::{GlobalMemoryStatusEx, MEMORYSTATUSEX};
    let mut m = MEMORYSTATUSEX { dwLength: std::mem::size_of::<MEMORYSTATUSEX>() as u32, ..Default::default() };
    unsafe { GlobalMemoryStatusEx(&mut m).ok()? };
    let gb = (1u64 << 30) as f64;
    let used = (m.ullTotalPhys - m.ullAvailPhys) as f64;
    Some((m.dwMemoryLoad as f32 / 100.0, used / gb, m.ullTotalPhys as f64 / gb))
}

/// Nome da saída de som padrão (ex.: "Fones de ouvido (Buds2 Pro)").
pub fn audio_output_name() -> Option<String> {
    use windows::Win32::Devices::FunctionDiscovery::PKEY_Device_FriendlyName;
    use windows::Win32::Media::Audio::{eConsole, eRender, IMMDeviceEnumerator, MMDeviceEnumerator};
    use windows::Win32::System::Com::{CoCreateInstance, CoInitializeEx, CLSCTX_ALL, COINIT_MULTITHREADED, STGM_READ};
    use windows::Win32::System::Com::StructuredStorage::PropVariantToStringAlloc;
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        let en: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL).ok()?;
        let dev = en.GetDefaultAudioEndpoint(eRender, eConsole).ok()?;
        let store = dev.OpenPropertyStore(STGM_READ).ok()?;
        let value = store.GetValue(&PKEY_Device_FriendlyName).ok()?;
        let text = PropVariantToStringAlloc(&value).ok()?;
        let s = text.to_string().ok();
        windows::Win32::System::Com::CoTaskMemFree(Some(text.0 as _));
        s
    }
}

/// Controle de volume geral da saída de som padrão (o do ícone de som da barra de tarefas).
fn endpoint_volume() -> Option<windows::Win32::Media::Audio::Endpoints::IAudioEndpointVolume> {
    use windows::Win32::Media::Audio::{eConsole, eRender, IMMDeviceEnumerator, MMDeviceEnumerator};
    use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_ALL};
    unsafe {
        let en: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL).ok()?;
        en.GetDefaultAudioEndpoint(eRender, eConsole).ok()?.Activate(CLSCTX_ALL, None).ok()
    }
}

pub fn master_volume() -> Option<f32> {
    unsafe { endpoint_volume()?.GetMasterVolumeLevelScalar().ok() }
}

pub fn set_master_volume(v: f32) {
    if let Some(e) = endpoint_volume() {
        unsafe { let _ = e.SetMasterVolumeLevelScalar(v.clamp(0.0, 1.0), std::ptr::null()); }
    }
}

/// Medidor de volume da saída de som padrão (o mesmo do mixer do Windows).
pub fn peak_meter() -> Option<windows::Win32::Media::Audio::Endpoints::IAudioMeterInformation> {
    use windows::Win32::Media::Audio::{eConsole, eRender, IMMDeviceEnumerator, MMDeviceEnumerator};
    use windows::Win32::System::Com::{CoCreateInstance, CoInitializeEx, CLSCTX_ALL, COINIT_MULTITHREADED};
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        let en: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL).ok()?;
        en.GetDefaultAudioEndpoint(eRender, eConsole).ok()?.Activate(CLSCTX_ALL, None).ok()
    }
}

/// Captura o som que está saindo do PC (loopback da saída padrão) e chama `on` com amostras mono
/// a cada ~15 ms (lista vazia quando nada tocou). Volta quando a saída padrão muda; erro se não der.
pub fn loopback(mut on: impl FnMut(&[f32], f32)) -> windows::core::Result<()> {
    use windows::Win32::Media::Audio::{
        eConsole, eRender, IAudioCaptureClient, IAudioClient, IMMDeviceEnumerator, MMDeviceEnumerator, AUDCLNT_BUFFERFLAGS_SILENT,
        AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK,
    };
    use windows::Win32::System::Com::{CoCreateInstance, CoInitializeEx, CoTaskMemFree, CLSCTX_ALL, COINIT_MULTITHREADED};
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        let en: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)?;
        let device_id = |en: &IMMDeviceEnumerator| -> Option<String> {
            let p = en.GetDefaultAudioEndpoint(eRender, eConsole).ok()?.GetId().ok()?;
            let s = p.to_string().ok();
            CoTaskMemFree(Some(p.0 as _));
            s
        };
        let id = device_id(&en);
        let client: IAudioClient = en.GetDefaultAudioEndpoint(eRender, eConsole)?.Activate(CLSCTX_ALL, None)?;
        let fmt = client.GetMixFormat()?;
        let (channels, rate, bits) = ((*fmt).nChannels as usize, (*fmt).nSamplesPerSec as f32, (*fmt).wBitsPerSample);
        let init = client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 2_000_000, 0, fmt, None);
        CoTaskMemFree(Some(fmt as _));
        init?;
        // O formato do mixer compartilhado é float de 32 bits; outro formato fica com o medidor de volume.
        if bits != 32 || channels == 0 { return Err(windows::Win32::Foundation::E_FAIL.into()); }
        let cap: IAudioCaptureClient = client.GetService()?;
        client.Start()?;
        let mut mono = Vec::new();
        let mut checked = std::time::Instant::now();
        loop {
            std::thread::sleep(std::time::Duration::from_millis(15));
            mono.clear();
            while cap.GetNextPacketSize()? > 0 {
                let (mut data, mut frames, mut flags) = (std::ptr::null_mut(), 0u32, 0u32);
                cap.GetBuffer(&mut data, &mut frames, &mut flags, None, None)?;
                if flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0 || data.is_null() {
                    mono.extend(std::iter::repeat_n(0.0, frames as usize));
                } else {
                    let all = std::slice::from_raw_parts(data as *const f32, frames as usize * channels);
                    mono.extend(all.chunks_exact(channels).map(|f| f.iter().sum::<f32>() / channels as f32));
                }
                cap.ReleaseBuffer(frames)?;
            }
            on(&mono, rate);
            if checked.elapsed().as_secs() >= 3 {
                checked = std::time::Instant::now();
                if device_id(&en) != id { let _ = client.Stop(); return Ok(()); } // trocou fone/caixa: recomeça
            }
        }
    }
}

/// Abre um app, arquivo, pasta ou site como o Explorer abriria.
pub fn open(target: &str) {
    unsafe { ShellExecuteW(Some(HWND::default()), &HSTRING::from("open"), &HSTRING::from(target), PCWSTR::null(), PCWSTR::null(), SW_SHOWNORMAL) };
}

/// Aperta e solta uma combinação de teclas (ex.: Win+Shift+R).
pub fn press_keys(keys: &[u8]) {
    unsafe {
        for &k in keys { keybd_event(k, 0, KEYBD_EVENT_FLAGS(0), 0); }
        for &k in keys.iter().rev() { keybd_event(k, 0, KEYEVENTF_KEYUP, 0); }
    }
}
