# Orbital Phone — R8/ProGuard rules for release builds.
#
# Activities, Services and other manifest-declared components are kept automatically
# by the AAPT-generated rules, so only add explicit keeps here for things reached via
# reflection or JNI. The app has no reflection-based entry points today; keep this list
# tight and add rules only when a release build crashes with a missing class/method.

# Keep Kotlin metadata so the Kotlin runtime/reflection helpers behave.
-keepattributes *Annotation*, Signature, InnerClasses, EnclosingMethod

# Line numbers for readable release stack traces (strip the source file name).
-keepattributes SourceFile,LineNumberTable
-renamesourcefileattribute SourceFile

# androidx / Kotlin stdlib ship their own consumer rules; nothing extra needed here.
