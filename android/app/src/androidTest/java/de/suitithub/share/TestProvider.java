package de.suitithub.share;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.res.AssetFileDescriptor;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;

import java.io.File;
import java.io.FileNotFoundException;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.util.HashSet;
import java.util.Random;
import java.util.Set;

/**
 * Serves files in all the ways real providers do: content://AUTHORITY/mode/size/name; the content is pseudo random
 * (seeded by the name). Runs in the test package's own process like a foreign app, which has no Kotlin runtime: Java only.
 */
public class TestProvider extends ContentProvider {
    public static final String AUTHORITY = "de.suitithub.share.test.provider";
    /** Same content, but reachable only with a URI grant. */
    public static final String GUARDED = "de.suitithub.share.test.guarded";

    public static Uri uri(String mode, int size, String name) {
        return uri(AUTHORITY, mode, size, name);
    }

    public static Uri uri(String authority, String mode, int size, String name) {
        return new Uri.Builder().scheme("content").authority(authority).appendPath(mode).appendPath("" + size).appendPath(name).build();
    }

    public static byte[] bytes(int size, String name) {
        byte[] b = new byte[size];
        new Random(name.hashCode()).nextBytes(b);
        return b;
    }

    private static final Set<String> opened = new HashSet<>();

    @Override
    public boolean onCreate() {
        return true;
    }

    private static String mode(Uri uri) {
        return uri.getPathSegments().get(0);
    }

    private static int size(Uri uri) {
        return Integer.parseInt(uri.getPathSegments().get(1));
    }

    private synchronized File file(Uri uri) throws FileNotFoundException {
        File f = new File(getContext().getCacheDir(), size(uri) + "-" + uri.getLastPathSegment());
        if (!f.exists()) write(f, bytes(size(uri), uri.getLastPathSegment()));
        return f;
    }

    private static void write(File f, byte[] b) throws FileNotFoundException {
        try (FileOutputStream o = new FileOutputStream(f)) {
            o.write(b);
        } catch (IOException e) {
            throw new FileNotFoundException(e.toString());
        }
    }

    @Override
    public Cursor query(Uri uri, String[] projection, String selection, String[] args, String sort) {
        MatrixCursor c;
        switch (mode(uri)) {
            case "nosize":
            case "pipe":
                c = new MatrixCursor(new String[]{OpenableColumns.DISPLAY_NAME});
                c.addRow(new Object[]{uri.getLastPathSegment()});
                return c;
            case "noname":
                c = new MatrixCursor(new String[]{"foo"});
                c.addRow(new Object[]{"bar"});
                return c;
            case "nullcursor":
                return null;
            case "denied":
                throw new SecurityException("denied");
            case "broken":
                throw new IllegalStateException("broken provider");
            default:
                c = new MatrixCursor(new String[]{OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE});
                c.addRow(new Object[]{uri.getLastPathSegment(), mode(uri).equals("wrongsize") ? 5L : (long) size(uri)});
                return c;
        }
    }

    @Override
    public String getType(Uri uri) {
        switch (mode(uri)) {
            case "noname":
                return "image/jpeg";
            case "broken":
                throw new IllegalStateException("broken provider");
            default:
                return null;
        }
    }

    /** Some providers (e.g. contacts as vCard) only hand out a slice of a file. */
    @Override
    public AssetFileDescriptor openAssetFile(Uri uri, String access) throws FileNotFoundException {
        if (!mode(uri).equals("asset")) return super.openAssetFile(uri, access);
        return new AssetFileDescriptor(ParcelFileDescriptor.open(file(uri), ParcelFileDescriptor.MODE_READ_ONLY), 0, size(uri));
    }

    @Override
    public ParcelFileDescriptor openFile(Uri uri, String access) throws FileNotFoundException {
        switch (mode(uri)) {
            case "missing":
                throw new FileNotFoundException("missing");
            case "denied":
                throw new SecurityException("denied");
            case "broken":
                throw new IllegalStateException("broken provider");
            case "once": // a permission that is gone after the first use
                synchronized (opened) {
                    if (!opened.add(uri.toString())) throw new SecurityException("only once");
                }
                break;
            case "change": // rewrites the plain file with zeros
                write(file(uri), new byte[size(uri)]);
                break;
            case "fresh": // restores the original content
                write(file(uri), bytes(size(uri), uri.getLastPathSegment()));
                break;
            case "pipeonce":
                synchronized (opened) {
                    if (!opened.add(uri.toString())) throw new SecurityException("only once");
                }
                // fall through
            case "pipe":
                final byte[] data = bytes(size(uri), uri.getLastPathSegment());
                try {
                    final ParcelFileDescriptor[] p = ParcelFileDescriptor.createPipe();
                    new Thread(() -> {
                        try (OutputStream o = new ParcelFileDescriptor.AutoCloseOutputStream(p[1])) {
                            o.write(data);
                        } catch (IOException ignored) {
                        }
                    }).start();
                    return p[0];
                } catch (IOException e) {
                    throw new FileNotFoundException(e.toString());
                }
            default:
        }
        return ParcelFileDescriptor.open(file(uri), ParcelFileDescriptor.MODE_READ_ONLY);
    }

    @Override
    public Uri insert(Uri uri, ContentValues values) {
        return null;
    }

    @Override
    public int delete(Uri uri, String selection, String[] args) {
        return 0;
    }

    @Override
    public int update(Uri uri, ContentValues values, String selection, String[] args) {
        return 0;
    }

    /** The guarded twin. */
    public static class Guarded extends TestProvider {
    }
}
