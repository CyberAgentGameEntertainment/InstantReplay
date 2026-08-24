package jp.co.cyberagent.unienc.harness;

/**
 * Shim that gives the native harness a JavaVM.
 *
 * <p>Loading the library is the whole point: {@code System.load} calls
 * {@code JNI_OnLoad}, which is where the MediaCodec backend picks up the
 * JavaVM it cannot work without. Everything else happens in native code.
 *
 * <p>Run through {@code app_process} so that no APK is needed; see
 * {@code scripts/android-device-test.sh}.
 */
public final class Harness {
    private Harness() {}

    private static native int run(String outputPath);

    public static void main(String[] args) {
        if (args.length != 2) {
            System.out.println("usage: Harness <library path> <output path>");
            System.exit(2);
        }

        System.load(args[0]);
        System.exit(run(args[1]));
    }
}
