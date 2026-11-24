package de.suitithub.share.ui

import android.Manifest
import android.content.Intent
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.repeatOnLifecycle
import de.suitithub.share.R
import de.suitithub.share.core.Engine
import de.suitithub.share.core.PairUri
import de.suitithub.share.shareApp
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
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
                    if (Build.VERSION.SDK_INT >= 33) askNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
                    shareApp.uiShown().use { e.hold().use { awaitCancellation() } }
                }
            }
        }
        setContent { ShareTheme { Surface { App() } } }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        readLink(intent)
    }

    private fun readLink(i: Intent?) {
        pairLink = i?.dataString?.let { PairUri.parse(it) }
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
            else -> TextButton(onClick = { settings = true }, modifier = Modifier.testTag("settings")) { Text(stringResource(R.string.settings)) }
        }
    }
}
