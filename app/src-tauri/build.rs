fn main() {
    // Release builds stamp the four-part installer version (e.g. 1.1.0.0) for update checks.
    println!("cargo:rerun-if-env-changed=MANGO_VERSION");
    tauri_build::build()
}
