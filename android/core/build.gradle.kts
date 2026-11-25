import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    alias(libs.plugins.kotlin.jvm)
    alias(libs.plugins.kotlin.serialization)
}

java {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

kotlin {
    compilerOptions { jvmTarget.set(JvmTarget.JVM_17) }
}

dependencies {
    api(libs.kotlinx.serialization.json)
    api(libs.kotlinx.coroutines.core)
    api(libs.okhttp)
    implementation(libs.okhttp.tls)
    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)
    testImplementation(libs.mockwebserver)
}

// the tests talk to the real server and CLI; no Go means the build fails instead of silently skipping
val goBin = providers.gradleProperty("goBin").orElse(providers.environmentVariable("GO_BIN")).orElse("go")
val goOut = layout.buildDirectory.dir("go")
val buildGo by tasks.registering(Exec::class) {
    val serverDir = rootDir.resolve("../server")
    inputs.files(fileTree(serverDir) { include("**/*.go", "go.mod", "go.sum") })
    outputs.dir(goOut)
    workingDir = serverDir
    commandLine(goBin.get(), "build", "-o", goOut.get().asFile.path + File.separator, "./cmd/share-server", "./cmd/share")
    environment("CGO_ENABLED", "0")
}

tasks.test {
    dependsOn(buildGo)
    systemProperty("share.go.dir", goOut.get().asFile.path)
    useJUnit()
    outputs.upToDateWhen { false }
    testLogging {
        events("failed")
        exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
    }
}

// a Kotlin device for the Windows client's interop tests
tasks.register<JavaExec>("peer") {
    classpath = sourceSets.test.get().runtimeClasspath
    mainClass = "de.suitithub.share.core.PeerMainKt"
    args = providers.environmentVariable("PEER_ARGS").getOrElse("").split("|")
    defaultCharacterEncoding = "UTF-8"
}
