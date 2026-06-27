import sys
import struct
import os

def inspect_nf1(file_path):
    if not os.path.exists(file_path):
        print(f"[-] File not found: {file_path}")
        return

    with open(file_path, "rb") as f:
        # 1. Read 4-byte Magic
        magic = f.read(4)
        if magic != b"NF1\x00":
            print(f"[-] Invalid Magic Header: {magic}. Expected b'NF1\\x00'")
            return

        print(f"[+] Valid FiveM NF1 Container detected.")

        # 2. Read standard container metadata
        try:
            version, entry_count, str_table_size = struct.unpack("<III", f.read(12))
            print(f" ├─ Container Version : {version}")
            print(f" ├─ Indexed Entries   : {entry_count}")
            print(f" └─ String Table Size : {str_table_size} bytes\n")
        except struct.error:
            print("[-] File is truncated or corrupted.")
            return

        # 3. Read the String Table block
        raw_string_table = f.read(str_table_size)
        
        # Split null-terminated strings
        file_names = [s.decode('utf-8', errors='ignore') for s in raw_string_table.split(b'\x00') if s]

        print("[*] Discovered Internal File Tree:")
        for idx, name in enumerate(file_names, 1):
            print(f"    {idx:02d}. {name}")

        # Quick Escrow Heuristic check
        remaining_bytes = f.read(1024)
        if b"CFX-ESCROW" in remaining_bytes or b"Tebex" in remaining_bytes:
            print("\n[!] WARNING: This bundle contains Cfx Asset Escrow encryption signatures.")
            print("    The filenames above are visible, but the script contents cannot be read.")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python nf1_viewer.py <path_to_resource_file>")
    else:
        inspect_nf1(sys.argv[1])