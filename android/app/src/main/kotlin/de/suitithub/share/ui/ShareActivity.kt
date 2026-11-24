package de.suitithub.share.ui

import android.content.ContentResolver
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.os.Parcelable
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.IntentCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import de.suitithub.share.R
import de.suitithub.share.core.ApiException
import de.suitithub.share.core.Engine
import de.suitithub.share.core.Kind
import de.suitithub.share.core.MAX_TEXT_BYTES
import de.suitithub.share.service.TransferService
import de.suitithub.share.shareApp
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import java.io.IOException

sealed interface SharePayload {
    data class Files(val uris: List<Uri>, val message: String) : SharePayload
    data class Text(val kind: Kind, val text: String) : SharePayload
    data object Empty : SharePayload

    /** Contains something that must not or cannot be read. */
    data object Rejected : SharePayload
    data object TooLong : SharePayload
}

/**
 * What another app wants to share. Only foreign content:// items are accepted: with file:// or one of our own providers
 * another app could make us send our private data.
 */
fun parseShare(intent: Intent, ownAuthority: (String) -> Boolean): SharePayload {
    val uris = LinkedHashSet<Uri>()
    when (intent.action) {
        Intent.ACTION_SEND -> IntentCompat.getParcelableExtra(intent, Intent.EXTRA_STREAM, Parcelable::class.java)?.let { if (it is Uri) uris += it }
        Intent.ACTION_SEND_MULTIPLE -> IntentCompat.getParcelableArrayListExtra(intent, Intent.EXTRA_STREAM, Parcelable::class.java)?.forEach { if (it is Uri) uris += it }
        else -> return SharePayload.Empty
    }
    val text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString().orEmpty()
    // with a text, the clip data is only the preview picture of the share sheet
    if (uris.isEmpty() && text.isBlank()) intent.clipData?.let { c -> (0 until c.itemCount).forEach { i -> c.getItemAt(i).uri?.let { uris += it } } }
    if (uris.isNotEmpty()) {
        if (uris.any { it.scheme != ContentResolver.SCHEME_CONTENT || ownAuthority(it.authority.orEmpty()) }) return SharePayload.Rejected
        return SharePayload.Files(uris.toList(), text.take(MAX_MESSAGE))
    }
    if (text.isBlank()) return SharePayload.Empty
    if (text.toByteArray().size > MAX_TEXT_BYTES) return SharePayload.TooLong
    val kind = Format.kindOf(text)
    return SharePayload.Text(kind, if (kind == Kind.LINK) text.trim() else text)
}

private const val MAX_MESSAGE = 1000

/** The sheet behind "Teilen → Share": choose devices, send, gone. */
class ShareActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val payload = parseShare(intent) { a -> packageManager.resolveContentProvider(a.substringAfterLast('@'), 0)?.packageName == packageName }
        lifecycleScope.launch {
            // connected while visible, so the device list is current
            repeatOnLifecycle(Lifecycle.State.STARTED) { shareApp.engine.collectLatest { e -> e?.hold()?.use { awaitCancellation() } } }
        }
        setContent { ShareTheme { Sheet { Content(payload) } } }
    }

    @Composable
    private fun Sheet(content: @Composable ColumnScope.() -> Unit) = Box(
        Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.4f)).pointerInput(Unit) { detectTapGestures { finish() } },
        contentAlignment = Alignment.BottomCenter,
    ) {
        Surface(
            // swallows taps that would otherwise close the sheet; not clickable, which would be one big button for screen readers
            Modifier.fillMaxWidth().pointerInput(Unit) { detectTapGestures { } },
            shape = RoundedCornerShape(topStart = 24.dp, topEnd = 24.dp),
        ) { Column(Modifier.safeDrawingPadding().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) { content() } }
    }

    @Composable
    private fun ColumnScope.Content(payload: SharePayload) {
        val engine by shareApp.engine.collectAsStateWithLifecycle()
        val e = engine
        when {
            payload is SharePayload.Empty -> Hint(R.string.nothing_to_share)
            payload is SharePayload.Rejected -> Hint(R.string.share_rejected)
            payload is SharePayload.TooLong -> Hint(R.string.text_too_long)
            e == null -> Hint(R.string.pair_first)
            else -> Choose(e, payload)
        }
    }

    @Composable
    private fun Hint(id: Int) {
        Text(stringResource(id))
        OutlinedButton(onClick = { finish() }, modifier = Modifier.testTag("shareClose")) { Text(stringResource(R.string.close)) }
    }

    @Composable
    private fun ColumnScope.Choose(e: Engine, payload: SharePayload) {
        val state by e.state.collectAsStateWithLifecycle()
        var selected by rememberSaveable { mutableStateOf(emptyList<String>()) }
        var all by rememberSaveable { mutableStateOf(false) }
        val targets = if (all) emptyList() else selected.filter { id -> state.devices.any { it.id == id } }

        Text(
            when (payload) {
                is SharePayload.Files -> if (payload.uris.size == 1) stringResource(R.string.send_one_file) else stringResource(R.string.send_files, payload.uris.size)
                is SharePayload.Text -> payload.text
                else -> ""
            },
            style = MaterialTheme.typography.titleMedium, maxLines = 2, overflow = TextOverflow.Ellipsis,
        )
        if (state.unauthorised) Text(stringResource(R.string.revoked), color = MaterialTheme.colorScheme.error)
        else if (state.devices.isEmpty()) Text(stringResource(if (state.connected) R.string.no_devices else R.string.connecting))
        LazyColumn(Modifier.weight(1f, fill = false).heightIn(max = 400.dp)) {
            targetItems(state.devices, if (all) emptyList() else selected, all, { selected = it; all = false }, { all = it })
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = { finish() }, modifier = Modifier.testTag("shareClose")) { Text(stringResource(R.string.cancel)) }
            Button(enabled = all || targets.isNotEmpty(), onClick = { send(e, payload, targets, all) }, modifier = Modifier.testTag("shareSend")) {
                Text(stringResource(R.string.send))
            }
        }
    }

    private fun send(e: Engine, payload: SharePayload, targets: List<String>, all: Boolean) {
        when (payload) {
            is SharePayload.Files -> TransferService.offer(this, payload.uris, targets, all, payload.message)
            is SharePayload.Text -> shareApp.scope.launch {
                try {
                    e.offerText(payload.kind, payload.text, targets, all)
                } catch (ex: ApiException) {
                    shareApp.tell(getString(R.string.send_failed), getString(R.string.server_refused, ex.message))
                } catch (_: IOException) {
                    shareApp.tell(getString(R.string.send_failed), getString(R.string.no_connection))
                }
            }
            else -> Unit
        }
        finish()
    }
}
