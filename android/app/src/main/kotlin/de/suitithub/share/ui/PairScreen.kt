package de.suitithub.share.ui

import android.graphics.Bitmap
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.google.zxing.BarcodeFormat
import com.google.zxing.qrcode.QRCodeWriter
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import de.suitithub.share.BuildConfig
import de.suitithub.share.R
import de.suitithub.share.core.Account
import de.suitithub.share.core.ApiException
import de.suitithub.share.core.Identity
import de.suitithub.share.core.PairUri
import de.suitithub.share.core.pair
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.IOException

private const val MAX_NAME_BYTES = 64

/** A pairing link only fills in the fields; nothing happens before the user taps "Koppeln". */
@Composable
fun PairScreen(prefill: PairUri?, hint: String?, onPaired: suspend (Account) -> Unit) {
    var url by rememberSaveable { mutableStateOf("") }
    var code by rememberSaveable { mutableStateOf("") }
    var name by rememberSaveable { mutableStateOf(Build.MODEL.orEmpty()) }
    var error by rememberSaveable { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current

    fun fill(u: PairUri) {
        url = u.url
        code = u.code
        error = null
    }
    LaunchedEffect(prefill) { prefill?.let { fill(it) } }
    val scan = rememberLauncherForActivityResult(ScanContract()) { result ->
        val text = result.contents ?: return@rememberLauncherForActivityResult
        PairUri.parse(text)?.let { fill(it) } ?: run { error = context.getString(R.string.not_a_pair_code) }
    }

    Column(
        Modifier.fillMaxSize().safeDrawingPadding().imePadding().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(stringResource(R.string.pair_title), style = MaterialTheme.typography.headlineSmall)
        hint?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        Text(stringResource(R.string.pair_help))
        OutlinedButton(
            onClick = { scan.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE).setBeepEnabled(false).setOrientationLocked(false).setPrompt("")) },
            modifier = Modifier.fillMaxWidth(),
        ) { Text(stringResource(R.string.scan_qr)) }
        OutlinedTextField(
            url, { url = it }, Modifier.fillMaxWidth().testTag("url"), label = { Text(stringResource(R.string.server_url)) },
            singleLine = true, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
        )
        OutlinedTextField(
            code, { code = it }, Modifier.fillMaxWidth().testTag("code"), label = { Text(stringResource(R.string.code)) },
            singleLine = true, keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Characters),
        )
        OutlinedTextField(
            name, { if (it.toByteArray().size <= MAX_NAME_BYTES) name = it }, Modifier.fillMaxWidth().testTag("name"),
            label = { Text(stringResource(R.string.device_name)) }, singleLine = true,
        )
        error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        Button(
            enabled = !busy && url.isNotBlank() && code.isNotBlank() && name.isNotBlank(),
            onClick = {
                busy = true
                error = null
                scope.launch {
                    try {
                        val acc = withContext(Dispatchers.Default) {
                            pair(url, code, name.trim(), Identity.generate(name), allowInsecureLocal = BuildConfig.DEBUG)
                        }
                        onPaired(acc)
                    } catch (e: IllegalArgumentException) {
                        error = context.getString(R.string.bad_address)
                    } catch (e: ApiException) {
                        error = when (e.status) {
                            403 -> context.getString(R.string.bad_code)
                            409 -> context.getString(R.string.name_taken)
                            400 -> context.getString(R.string.bad_name)
                            else -> context.getString(R.string.server_refused, e.message)
                        }
                    } catch (e: IOException) {
                        error = context.getString(R.string.server_unreachable)
                    } finally {
                        busy = false
                    }
                }
            },
            modifier = Modifier.fillMaxWidth().testTag("pair"),
        ) { Text(stringResource(R.string.pair)) }
    }
}

/** Black on white, whatever the theme, so every camera can read it. */
fun qrBitmap(text: String, size: Int): Bitmap {
    val m = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size)
    val px = IntArray(m.width * m.height) { if (m.get(it % m.width, it / m.width)) 0xFF000000.toInt() else 0xFFFFFFFF.toInt() }
    return Bitmap.createBitmap(px, m.width, m.height, Bitmap.Config.ARGB_8888)
}
