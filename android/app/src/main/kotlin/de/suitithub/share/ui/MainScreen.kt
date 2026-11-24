package de.suitithub.share.ui

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import de.suitithub.share.R
import de.suitithub.share.core.Device
import de.suitithub.share.core.Download
import de.suitithub.share.core.EngineState
import de.suitithub.share.core.Kind
import de.suitithub.share.core.MAX_TEXT_BYTES
import de.suitithub.share.core.Phase
import de.suitithub.share.core.Status
import de.suitithub.share.core.Transfer
import kotlinx.coroutines.delay
import java.time.Instant

interface MainActions {
    fun accept(id: String)
    fun decline(id: String)
    fun cancelOutgoing(id: String)
    fun cancelDownload(id: String)
    fun dismiss(id: String)
    fun sendFiles(uris: List<Uri>, targets: List<String>, broadcast: Boolean)
    fun sendText(text: String, targets: List<String>, broadcast: Boolean)
    fun openResult(d: Download)
    fun openSettings()
}

@Composable
fun MainScreen(state: EngineState, actions: MainActions) {
    var now by remember { mutableStateOf(Instant.now()) }
    LaunchedEffect(Unit) {
        while (true) {
            delay(1000)
            now = Instant.now()
        }
    }
    var selected by rememberSaveable { mutableStateOf(emptyList<String>()) }
    var all by rememberSaveable { mutableStateOf(false) }
    var detail by rememberSaveable { mutableStateOf<String?>(null) }
    var writing by rememberSaveable { mutableStateOf(false) }
    val targets = if (all) emptyList() else selected.filter { id -> state.devices.any { it.id == id } }
    val canSend = all || targets.isNotEmpty()
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        if (uris.isNotEmpty()) actions.sendFiles(uris, targets, all)
    }

    LazyColumn(
        Modifier.fillMaxSize().safeDrawingPadding(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        item {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(stringResource(R.string.app_name), style = MaterialTheme.typography.headlineSmall)
                    Text(stringResource(if (state.connected) R.string.connected else R.string.connecting), style = MaterialTheme.typography.bodySmall)
                }
                TextButton(onClick = actions::openSettings, modifier = Modifier.testTag("settings")) { Text(stringResource(R.string.settings)) }
            }
        }

        item { Heading(R.string.incoming) }
        if (state.incoming.isEmpty()) item { Text(stringResource(R.string.no_incoming)) }
        items(state.incoming, key = { "in:" + it.id }) { t -> Incoming(t, state, now, actions) { detail = t.id } }

        if (state.outgoing.isNotEmpty() || state.downloads.isNotEmpty()) item { Heading(R.string.running) }
        items(state.outgoing, key = { "out:" + it.id }) { Outgoing(it, state, now, actions) }
        items(state.downloads, key = { "dl:" + it.transfer.id }) { DownloadRow(it, state, actions) }

        item { Heading(R.string.send_to) }
        if (state.devices.isEmpty()) item { Text(stringResource(R.string.no_devices)) }
        targetItems(state.devices, if (all) emptyList() else selected, all, { selected = it; all = false }, { all = it })
        item {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(enabled = canSend, onClick = { pick.launch(arrayOf("*/*")) }, modifier = Modifier.testTag("sendFiles")) { Text(stringResource(R.string.files)) }
                OutlinedButton(enabled = canSend, onClick = { writing = true }, modifier = Modifier.testTag("sendText")) { Text(stringResource(R.string.text_or_link)) }
            }
        }
    }

    state.incoming.firstOrNull { it.id == detail }?.let { t -> Details(t, state, now, actions) { detail = null } }
    if (writing) {
        TextDialog({ writing = false }) {
            writing = false
            actions.sendText(it, targets, all)
        }
    }
}

@Composable
private fun Heading(id: Int) = Text(stringResource(id), Modifier.padding(top = 8.dp), style = MaterialTheme.typography.titleMedium)

private fun EngineState.name(id: String) = names[id] ?: "Unbekanntes Gerät"

/** Check boxes for all devices and "all"; also used by the share sheet. */
fun androidx.compose.foundation.lazy.LazyListScope.targetItems(
    devices: List<Device>,
    selected: List<String>,
    all: Boolean,
    onSelect: (List<String>) -> Unit,
    onAll: (Boolean) -> Unit,
) {
    items(devices, key = { "target:" + it.id }) { d ->
        Target("target:" + d.id, d.name + if (d.online) " ●" else "", d.id in selected) { on -> onSelect(if (on) selected + d.id else selected - d.id) }
    }
    if (devices.isNotEmpty()) item { Target("target:all", stringResource(R.string.all_devices), all, onAll) }
}

@Composable
private fun Target(tag: String, label: String, on: Boolean, onChange: (Boolean) -> Unit) = Row(
    Modifier.fillMaxWidth().heightIn(min = 48.dp).testTag(tag).toggleable(on, role = Role.Checkbox, onValueChange = onChange),
    verticalAlignment = Alignment.CenterVertically,
) {
    Checkbox(on, null)
    Text(label, Modifier.padding(start = 8.dp))
}

@Composable
private fun Incoming(t: Transfer, state: EngineState, now: Instant, actions: MainActions, onDetail: () -> Unit) = Card(Modifier.fillMaxWidth().clickable(onClick = onDetail)) {
    Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(Format.offerTitle(t, state.name(t.senderId)), style = MaterialTheme.typography.titleSmall)
        Text(Format.offerBody(t), maxLines = 3, overflow = TextOverflow.Ellipsis)
        Text(Format.remaining(t.expiresAt, now), style = MaterialTheme.typography.bodySmall)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = { actions.decline(t.id) }, modifier = Modifier.testTag("decline:" + t.id)) { Text(stringResource(R.string.decline)) }
            Button(onClick = { actions.accept(t.id) }, modifier = Modifier.testTag("accept:" + t.id)) { Text(stringResource(R.string.accept)) }
        }
    }
}

@Composable
private fun Details(t: Transfer, state: EngineState, now: Instant, actions: MainActions, onClose: () -> Unit) = AlertDialog(
    onDismissRequest = onClose,
    title = { Text(Format.offerTitle(t, state.name(t.senderId))) },
    text = {
        Column(Modifier.heightIn(max = 400.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            if (t.message.isNotBlank()) Text(t.message)
            if (t.kind != Kind.FILES) Text(t.text)
            t.files.take(MAX_LISTED).forEach { Text("${it.name} · ${Format.size(it.size)}") }
            if (t.files.size > MAX_LISTED) Text(stringResource(R.string.more_files, t.files.size - MAX_LISTED))
            if (t.files.size > 1) Text(Format.size(t.totalSize), style = MaterialTheme.typography.titleSmall)
            Text(Format.remaining(t.expiresAt, now), style = MaterialTheme.typography.bodySmall)
        }
    },
    confirmButton = {
        TextButton(onClick = {
            onClose()
            actions.accept(t.id)
        }, modifier = Modifier.testTag("detailAccept")) { Text(stringResource(R.string.accept)) }
    },
    dismissButton = {
        TextButton(onClick = {
            onClose()
            actions.decline(t.id)
        }, modifier = Modifier.testTag("detailDecline")) { Text(stringResource(R.string.decline)) }
    },
)

@Composable
private fun Outgoing(t: Transfer, state: EngineState, now: Instant, actions: MainActions) = Card(Modifier.fillMaxWidth()) {
    Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text("↑ " + Format.shortName(t), style = MaterialTheme.typography.titleSmall)
        if (t.kind != Kind.FILES) Text(t.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
        for (d in t.deliveries) {
            val percent = if (d.status == Status.DOWNLOADING && t.kind == Kind.FILES) " ${Format.percent(d.bytes, t.totalSize)} %" else ""
            Text("${state.name(d.deviceId)}: ${Format.status(d.status, d.path)}$percent")
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                if (t.expiresAt > now) Format.remaining(t.expiresAt, now) else stringResource(R.string.transferring),
                Modifier.weight(1f), style = MaterialTheme.typography.bodySmall,
            )
            TextButton(onClick = { actions.cancelOutgoing(t.id) }, modifier = Modifier.testTag("cancel:" + t.id)) { Text(stringResource(R.string.cancel)) }
        }
    }
}

@Composable
private fun DownloadRow(d: Download, state: EngineState, actions: MainActions) = Card(Modifier.fillMaxWidth()) {
    val t = d.transfer
    Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text("↓ ${Format.shortName(t)} (${state.name(t.senderId)})", style = MaterialTheme.typography.titleSmall)
        when (d.phase) {
            Phase.RUNNING -> {
                val p = Format.percent(d.bytes, t.totalSize)
                LinearProgressIndicator(progress = { p / 100f }, modifier = Modifier.fillMaxWidth())
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(listOf("$p %", Format.path(d.path)).filter { it.isNotEmpty() }.joinToString(" · "), Modifier.weight(1f))
                    TextButton(onClick = { actions.cancelDownload(t.id) }, modifier = Modifier.testTag("cancel:" + t.id)) { Text(stringResource(R.string.cancel)) }
                }
            }
            Phase.DONE -> Row(verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(R.string.in_downloads), Modifier.weight(1f))
                TextButton(onClick = { actions.openResult(d) }) { Text(stringResource(if (Results.isApk(d)) R.string.install else R.string.open)) }
                Dismiss(t.id, actions)
            }
            Phase.FAILED -> Row(verticalAlignment = Alignment.CenterVertically) {
                Text(d.error.orEmpty(), Modifier.weight(1f), color = MaterialTheme.colorScheme.error)
                Dismiss(t.id, actions)
            }
        }
    }
}

@Composable
private fun Dismiss(id: String, actions: MainActions) =
    TextButton(onClick = { actions.dismiss(id) }, modifier = Modifier.testTag("dismiss:$id")) { Text(stringResource(R.string.close)) }

@Composable
private fun TextDialog(onClose: () -> Unit, onSend: (String) -> Unit) {
    var text by rememberSaveable { mutableStateOf("") }
    val tooLong = text.toByteArray().size > MAX_TEXT_BYTES
    AlertDialog(
        onDismissRequest = onClose,
        text = {
            OutlinedTextField(
                text, { text = it }, Modifier.fillMaxWidth().testTag("text"), label = { Text(stringResource(R.string.text_or_link)) },
                maxLines = 8, isError = tooLong, supportingText = { if (tooLong) Text(stringResource(R.string.text_too_long)) },
            )
        },
        confirmButton = { TextButton(enabled = text.isNotBlank() && !tooLong, onClick = { onSend(text) }, modifier = Modifier.testTag("sendTextNow")) { Text(stringResource(R.string.send)) } },
        dismissButton = { TextButton(onClick = onClose) { Text(stringResource(R.string.cancel)) } },
    )
}

private const val MAX_LISTED = 100
