package de.suitithub.share.ui

import android.content.Intent
import android.provider.Settings
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalResources
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import de.suitithub.share.R
import de.suitithub.share.core.ApiException
import de.suitithub.share.core.Engine
import de.suitithub.share.core.PairCodeResponse
import de.suitithub.share.core.PairUri
import de.suitithub.share.service.pushAvailable
import de.suitithub.share.shareApp
import kotlinx.coroutines.launch
import java.io.IOException

@Composable
fun SettingsScreen(engine: Engine, onBack: () -> Unit) {
    BackHandler(onBack = onBack)
    val context = LocalContext.current
    val res = LocalResources.current
    val app = context.shareApp
    val scope = rememberCoroutineScope()
    var name by rememberSaveable { mutableStateOf(app.deviceName) }
    var newCode by remember { mutableStateOf<PairCodeResponse?>(null) }
    var message by remember { mutableStateOf<String?>(null) }
    var asking by rememberSaveable { mutableStateOf(false) }

    fun run(block: suspend () -> Unit) {
        message = null
        scope.launch {
            try {
                block()
            } catch (e: ApiException) {
                message = if (e.status == 409) res.getString(R.string.name_taken) else res.getString(R.string.server_refused, e.message)
            } catch (e: IOException) {
                message = res.getString(R.string.no_connection)
            }
        }
    }

    Column(
        Modifier.fillMaxSize().safeDrawingPadding().imePadding().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onBack) { Text("←") }
            Text(stringResource(R.string.settings), style = MaterialTheme.typography.headlineSmall)
        }
        Text(stringResource(R.string.server_is, engine.api.account.serverUrl))
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(
                name, { if (it.toByteArray().size <= 64) name = it }, Modifier.weight(1f).testTag("deviceName"),
                label = { Text(stringResource(R.string.device_name)) }, singleLine = true,
            )
            Button(enabled = name.isNotBlank() && name.trim() != app.deviceName, onClick = { run { app.rename(engine, name.trim()); message = res.getString(R.string.saved) } }, modifier = Modifier.testTag("rename")) {
                Text(stringResource(R.string.save))
            }
        }
        message?.let { Text(it, color = MaterialTheme.colorScheme.primary) }

        HorizontalDivider()
        OutlinedButton(onClick = { run { newCode = engine.api.pairCode() } }, modifier = Modifier.fillMaxWidth().testTag("addDevice")) { Text(stringResource(R.string.add_device)) }
        newCode?.let { c ->
            val uri = PairUri(engine.api.account.serverUrl, c.code).toString()
            Text(stringResource(R.string.add_device_help))
            Text(c.code, Modifier.testTag("newCode"), style = MaterialTheme.typography.headlineMedium)
            Image(remember(uri) { qrBitmap(uri, 600) }.asImageBitmap(), contentDescription = stringResource(R.string.qr), modifier = Modifier.size(240.dp).testTag("qr"))
        }

        HorizontalDivider()
        Text(stringResource(if (pushAvailable(context)) R.string.push_on else R.string.push_off))
        if (!app.notifications.enabled) {
            Text(stringResource(R.string.notifications_off), color = MaterialTheme.colorScheme.error)
            OutlinedButton(onClick = {
                context.startActivity(Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName))
            }) { Text(stringResource(R.string.allow_notifications)) }
        }

        HorizontalDivider()
        OutlinedButton(onClick = { asking = true }, modifier = Modifier.fillMaxWidth().testTag("unpair")) { Text(stringResource(R.string.unpair)) }
    }

    if (asking) {
        AlertDialog(
            onDismissRequest = { asking = false },
            title = { Text(stringResource(R.string.unpair_title)) },
            text = { Text(stringResource(R.string.unpair_text)) },
            confirmButton = {
                TextButton(onClick = {
                    asking = false
                    app.unpair(engine)
                }) { Text(stringResource(R.string.unpair)) }
            },
            dismissButton = { TextButton(onClick = { asking = false }) { Text(stringResource(R.string.cancel)) } },
        )
    }
}
