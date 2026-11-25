fn main() {
    // Estilo escuro nos campos de texto (o notch é preto).
    let config = slint_build::CompilerConfiguration::new().with_style("fluent-dark".into());
    slint_build::compile_with_config("ui/notch.slint", config).expect("interface do notch");
}
