package de.suitithub.share;

import android.app.Activity;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;

import java.util.ArrayList;

/** A foreign app that shares files with a temporary permission, then goes away. Java only, see TestProvider. */
public class TestSenderActivity extends Activity {
    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        ArrayList<Uri> uris = new ArrayList<>();
        for (String s : getIntent().getStringArrayListExtra("uris")) uris.add(Uri.parse(s));
        Intent i = new Intent(uris.size() == 1 ? Intent.ACTION_SEND : Intent.ACTION_SEND_MULTIPLE);
        i.setType("*/*");
        if (uris.size() == 1) i.putExtra(Intent.EXTRA_STREAM, uris.get(0));
        else i.putParcelableArrayListExtra(Intent.EXTRA_STREAM, uris);
        i.setPackage("de.suitithub.share");
        i.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        startActivity(i);
        finish();
    }
}
