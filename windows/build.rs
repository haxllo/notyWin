fn main() {
    slint_build::compile("ui/noty.slint").expect("failed to compile Slint UI");
    println!("cargo:rerun-if-changed=ui/noty.slint");
}
