package de.suitithub.share.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Log
import de.suitithub.share.core.Account
import de.suitithub.share.core.Identity
import org.json.JSONObject
import java.io.File
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** The pairing (token, certificate, key), encrypted with a key that never leaves the Android Keystore. */
class SecureStore(context: Context, fileName: String = "account.bin") {
    private val file = File(context.filesDir, fileName)

    /** Null if not paired or if the file cannot be decrypted (then the device has to be paired again). */
    @Synchronized fun load(): Account? {
        if (!file.exists()) return null
        return try {
            val raw = file.readBytes()
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, raw, 0, IV))
            val o = JSONObject(String(cipher.doFinal(raw, IV, raw.size - IV)))
            Account(
                o.getString("server_url"), o.getString("device_id"), o.getString("token"), o.getString("name"),
                Identity.load(o.getString("cert"), o.getString("key")),
            )
        } catch (e: Exception) { // keystore and parser failures of all kinds
            Log.w("SecureStore", "cannot read pairing: ${e.javaClass.simpleName}")
            null
        }
    }

    @Synchronized fun save(a: Account) {
        val o = JSONObject()
            .put("server_url", a.serverUrl).put("device_id", a.deviceId).put("token", a.token).put("name", a.name)
            .put("cert", a.identity.certPem).put("key", a.identity.keyPem)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val tmp = File(file.path + ".tmp")
        tmp.writeBytes(cipher.iv + cipher.doFinal(o.toString().toByteArray()))
        check(tmp.renameTo(file)) { "cannot store pairing" }
    }

    @Synchronized fun clear() {
        file.delete()
    }

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance(PROVIDER).apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, PROVIDER).run {
            init(
                KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .build(),
            )
            generateKey()
        }
    }

    private companion object {
        const val PROVIDER = "AndroidKeyStore"
        const val ALIAS = "share-config"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV = 12
    }
}
