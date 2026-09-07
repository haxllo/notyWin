fn main() {
    slint_build::compile("src/ui/noty.slint").expect("failed to compile Slint UI");
    println!("cargo:rerun-if-changed=src/ui/noty.slint");
}