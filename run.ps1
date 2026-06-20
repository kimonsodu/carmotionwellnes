Set-Location android

./gradlew assembleDebug

adb install -r app/build/outputs/apk/debug/app-debug.apk

adb shell monkey -p com.steady.phone -c android.intent.category.LAUNCHER 1