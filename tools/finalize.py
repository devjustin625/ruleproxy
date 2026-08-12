"""收尾脚本：单实例冒烟测试 + 重新打包上传 zip + 更新桌面上传文件夹。"""
import os
import shutil
import subprocess
import sys
import time
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APP_EXE = os.path.join(ROOT, "dist", "RuleProxy.exe")
SETUP_EXE = os.path.join(ROOT, "dist", "RuleProxy-Setup.exe")
ZIP_PATH = os.path.join(ROOT, "RuleProxy-src.zip")
DESKTOP_DIR = os.path.join(os.environ.get("USERPROFILE", ""), "Desktop", "RuleProxy-上传")

EXCLUDE_DIRS = {"build", "dist", "__pycache__", ".pytest_cache", ".venv", ".git"}
EXCLUDE_FILES = {"icon.png", "RuleProxy-src.zip", "RuleProxy.spec", "RuleProxy-Setup.spec",
                 "_build_app.log", "_build_setup.log", "_t.txt", "_verify.py", "finalize.py"}


def selftest_single_instance() -> None:
    """启动 exe 两次：第一次应常驻，第二次应检测到多开并退出。"""
    def alive(p):
        return p.poll() is None

    p1 = subprocess.Popen([APP_EXE])
    time.sleep(6)
    print("first instance alive:", alive(p1))
    p2 = subprocess.Popen([APP_EXE])
    time.sleep(6)
    print("second instance alive:", alive(p2), "| second exitcode:", p2.poll())
    print("first still alive after 2nd launch:", alive(p1))
    for p in (p1, p2):
        if alive(p):
            p.terminate()
            try:
                p.wait(timeout=5)
            except subprocess.TimeoutExpired:
                p.kill()


def make_zip() -> None:
    if os.path.exists(ZIP_PATH):
        os.remove(ZIP_PATH)
    count = 0
    with zipfile.ZipFile(ZIP_PATH, "w", zipfile.ZIP_DEFLATED) as zf:
        for dirpath, dirnames, filenames in os.walk(ROOT):
            dirnames[:] = [d for d in dirnames if d not in EXCLUDE_DIRS]
            for fn in filenames:
                if fn in EXCLUDE_FILES or fn.endswith(".spec"):
                    continue
                full = os.path.join(dirpath, fn)
                rel = os.path.relpath(full, ROOT)
                zf.write(full, os.path.join("RuleProxy", rel))
                count += 1
    print("zip written:", ZIP_PATH, "| files:", count, "| %.1f MB" % (os.path.getsize(ZIP_PATH) / 1024 / 1024))


def update_desktop() -> None:
    os.makedirs(DESKTOP_DIR, exist_ok=True)
    shutil.copy2(ZIP_PATH, os.path.join(DESKTOP_DIR, "RuleProxy-src.zip"))
    shutil.copy2(SETUP_EXE, os.path.join(DESKTOP_DIR, "RuleProxy-Setup.exe"))
    print("desktop folder updated:", DESKTOP_DIR)
    for n in os.listdir(DESKTOP_DIR):
        p = os.path.join(DESKTOP_DIR, n)
        print("  -", n, "%.1f MB" % (os.path.getsize(p) / 1024 / 1024))


if __name__ == "__main__":
    if "--selftest" in sys.argv:
        selftest_single_instance()
    make_zip()
    update_desktop()
    print("DONE")
