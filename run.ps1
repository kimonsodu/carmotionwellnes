$startDir = Get-Location
try {
    Set-Location android

    ./gradlew assembleDebug

    adb install -r app/build/outputs/apk/debug/app-debug.apk

    adb shell monkey -p com.orbital.phone -c android.intent.category.LAUNCHER 1
}
finally {
    Set-Location $startDir
}
