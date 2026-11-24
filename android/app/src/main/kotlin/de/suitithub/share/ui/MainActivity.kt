package de.suitithub.share.ui

import android.Manifest
import android.content.ActivityNotFoundException
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.repeatOnLifecycle
import de.suitithub.share.R
import de.suitithub.share.core.ApiException
import de.suitithub.share.core.Download
import de.suitithub.share.core.Engine
import de.suitithub.share.core.Kind
import de.suitithub.share.core.PairUri
import de.suitithub.share.service.TransferService
import de.suitithub.share.shareApp
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import java.io.IOException

class MainActivity : ComponentActivity() {
    private var asked = false
    private var pairLink by mutableStateOf<PairUri?>(null)
    private val askNotifications = registerForActivityResult(ActivityResultContracts.RequestPermission()) {}

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        if (savedInstanceState == null) readLink(intent)
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                // connected and silent (no notifications) while the screen is visible
                shareApp.engine.collectLatest { e ->
                    if (e == null) return@collectLatest
                    askForNotifications()
                    shareApp.ensureService()
                    shareApp.uiShown().use { e.hold().use { awaitCancellation() } }
                }
            }
        }
        setContent { ShareTheme { Surface { App() } } }
    }

    /** Once per start of the screen; the system itself stops asking after two refusals. */
    private fun askForNotifications() {
        if (asked || Build.VERSION.SDK_INT < 33 || checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) return
        asked = true
        askNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        readLink(intent)
    }

    private fun readLink(i: Intent?) {
        pairLink = i?.dataString?.let { PairUri.parse(it) }
    }

    private inner class Actions(private val e: Engine, private val settings: () -> Unit) : MainActions {
        /** Outlives the activity: an answer must not get lost when the user leaves. */
        private fun run(block: suspend () -> Unit) {
            shareApp.scope.launch {
                try {
                    block()
                } catch (ex: ApiException) {
                    shareApp.tell(getString(R.string.server_refused, ex.message))
                } catch (_: IOException) {
                    shareApp.tell(getString(R.string.no_connection))
                }
            }
        }

        override fun accept(id: String) = run { e.accept(id) }
        override fun decline(id: String) = run { e.decline(id) }
        override fun cancelOutgoing(id: String) = run { e.cancelOutgoing(id) }
        override fun cancelDownload(id: String) = e.cancelDownload(id)
        override fun dismiss(id: String) = e.dismiss(id)
        override fun sendFiles(uris: List<Uri>, targets: List<String>, broadcast: Boolean) = TransferService.offer(this@MainActivity, uris, targets, broadcast, "")
        override fun sendText(text: String, targets: List<String>, broadcast: Boolean) = run {
            val kind = Format.kindOf(text)
            e.offerText(kind, if (kind == Kind.LINK) text.trim() else text, targets, broadcast)
        }

        override fun openResult(d: Download) {
            if (!Results.open(this@MainActivity, d)) shareApp.tell(getString(R.string.cannot_open))
        }

        override fun openSettings() = settings()
        override fun openLink(url: String) {
            try {
                startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)))
            } catch (_: ActivityNotFoundException) {
                shareApp.tell(getString(R.string.cannot_open_link))
            }
        }

        override fun copy(text: String) {
            shareApp.copy(text)
            shareApp.tell(getString(R.string.copied))
        }
    }

    @Composable
    private fun App() {
        val engine by shareApp.engine.collectAsStateWithLifecycle()
        val e = engine
        if (e == null) Pair(null) else key(e) { Paired(e) }
    }

    @Composable
    private fun Pair(hint: String?) = PairScreen(pairLink, hint) {
        pairLink = null
        shareApp.setAccount(it)
    }

    @Composable
    private fun Paired(e: Engine) {
        val state by e.state.collectAsStateWithLifecycle()
        var settings by rememberSaveable { mutableStateOf(false) }
        when {
            state.unauthorised -> Pair(stringResource(R.string.revoked))
            settings -> SettingsScreen(e) { settings = false }
            else -> MainScreen(state, remember(e) { Actions(e) { settings = true } })
        }
    }
}
