import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.google.services) apply false
}

// push is optional: without the file the app builds and runs, just without Firebase
if (file("google-services.json").exists()) apply(plugin = "com.google.gms.google-services")

// see docs/android.md; without the file the release APK stays unsigned
val signing = rootProject.file("keystore.properties").takeIf { it.exists() }?.let { f -> Properties().apply { f.inputStream().use { load(it) } } }
if (signing == null) logger.info("keystore.properties is missing: the release APK will be unsigned")

android {
    namespace = "de.suitithub.share"
    compileSdk = 37

    defaultConfig {
        applicationId = "de.suitithub.share"
        minSdk = 29
        targetSdk = 36 // 37 makes local network access a runtime permission
        versionCode = 1
        versionName = "1.0"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }
    signingConfigs {
        if (signing != null) create("release") {
            storeFile = rootProject.file(signing.getProperty("storeFile"))
            storePassword = signing.getProperty("storePassword")
            keyAlias = signing.getProperty("keyAlias")
            keyPassword = signing.getProperty("keyPassword")
        }
    }
    buildTypes {
        debug {
            buildConfigField("boolean", "ALLOW_LOCAL_HTTP", "true")
        }
        release {
            buildConfigField("boolean", "ALLOW_LOCAL_HTTP", "false")
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.findByName("release")
        }
        // like release, but may talk to a test server on the development machine
        create("minified") {
            initWith(getByName("release"))
            buildConfigField("boolean", "ALLOW_LOCAL_HTTP", "true")
            signingConfig = signingConfigs.getByName("debug")
            matchingFallbacks += "release"
        }
    }
    sourceSets.getByName("minified").res.directories.add("src/debug/res")
    buildFeatures {
        compose = true
        buildConfig = true
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    lint {
        // an old Fragment version comes in through other libraries; this app has no FragmentActivity
        disable += "InvalidFragmentVersionForActivityResult"
    }
    packaging {
        resources.excludes += listOf("META-INF/versions/9/OSGI-INF/MANIFEST.MF", "META-INF/LICENSE*", "META-INF/NOTICE*")
    }
}

dependencies {
    implementation(project(":core"))
    implementation(libs.androidx.core)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.compose)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.material3)
    implementation(libs.firebase.messaging)
    implementation(libs.zxing)

    testImplementation(libs.junit)

    androidTestImplementation(platform(libs.compose.bom))
    androidTestImplementation(libs.compose.ui.test.junit4)
    androidTestImplementation(libs.androidx.test.runner)
    androidTestImplementation(libs.androidx.test.rules)
    androidTestImplementation(libs.androidx.test.junit)
    androidTestImplementation(libs.uiautomator)
    androidTestImplementation(libs.kotlinx.coroutines.test)
    debugImplementation(libs.compose.ui.test.manifest)
}
