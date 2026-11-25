fn main() {
    // Estilo escuro nos campos de texto (o notch é preto).
    let config = slint_build::CompilerConfiguration::new().with_style("fluent-dark".into());
    slint_build::compile_with_config("ui/notch.slint", config).expect("interface do notch");
    // Ícone do Orbi no .exe (Explorer, atalhos, instalador). Só ao compilar no Windows (precisa do rc.exe).
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") && cfg!(windows) {
        winresource::WindowsResource::new().set_icon("../orbi.ico").compile().expect("ícone do .exe");
    }
}
