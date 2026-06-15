#if EXTERNAL_GAME_ACTIVITY_CODE
#include "UGAApplication.h"
#include "UGADebug.h"
#include "game-activity/native_app_glue/android_native_app_glue.h"
#include <unistd.h>
#include <android/log.h>

// Keep in sync with InitializationStatus enum in UnityPlayerForGameActivity.java
enum class InitializationStatus : int
{
    NotInitialized = -1,
    Success = 0,
    Failure = 1
};

extern "C" JNIEXPORT void Java_com_unity3d_player_UnityPlayerGameActivity_UnityInitializeFromUIThead()
{
    Unity::SetThisThreadAsUIThread();
}

static InitializationStatus s_InitializationStatus = InitializationStatus::NotInitialized;

extern "C" JNIEXPORT void Java_com_unity3d_player_UnityPlayerForGameActivity_nativeUnityPlayerForGameActivityInitialized(JNIEnv*, jobject, jint intializationStatus)
{
    // Safeguard the initialization sequence
    // [Java][MainThread]  UnityPlayerGameActivity is created
    // [Java][MainThread]  UnityPlayerForGameActivity is created
    // [C++][MainThread]   libmain.so loads libunity.so
    // [C++][MainThread]   libunity.so JNI_OnLoad is called
    // [C++][GameThread]   libgame.so android_main is called (must be called only after creation of UnityPlayerForGameActivity)
    // [C++][GameThread]   libunity.so Unity Engine is initialized

    s_InitializationStatus = (InitializationStatus)intializationStatus;
}

// We use this loop when Unity failed to correctly initialize and is showing an alert dialog for the user
// Without this loop, the app would simply crash and exit, with relevant information being only in logcat
// Example usage:
// - If the application is installed not through normal way, sometimes the necessary native libraries are missing (For ex., only ARM64 libraries are present, but device only support ARMv7)
//   In cases like this we show a dialog with information the necessary libraries are missing and that app was incorrectly installed
// Also we use this loop after MainLoop to ensure that all events are processed
void IdleLoop(struct android_app* app)
{
    UNITY_LOG_INFO("Starting Idle Loop");
    app->onAppCmd = NULL;
    app->userData = NULL;

    bool looping = true;
    while (looping) {
        int events;
        struct android_poll_source *source;

        while (looping && (ALooper_pollOnce(-1, NULL, &events, (void **) &source)) >= 0) {
            if (source != NULL)
                source->process(app, source);

            if (app->destroyRequested)
                looping = false;
        }
    }
}

void UnityGameActivityPluginLoad(Unity::UnityApplication& application) __attribute__((weak));
void UnityGameActivityPluginUnload(Unity::UnityApplication& application) __attribute__((weak));

void MainLoop(struct android_app* app)
{
    Unity::SetThisThreadAsMainThread();

    auto instance = Unity::UnityApplication::CreateInstance(app);
    if (instance == NULL) {
        UNITY_FATAL_ERROR("Couldn't create Unity application instance");
    }

    if (UnityGameActivityPluginLoad != NULL)
        UnityGameActivityPluginLoad(*instance);

    instance->Loop();

    if (UnityGameActivityPluginUnload != NULL)
        UnityGameActivityPluginUnload(*instance);
}

static bool s_Destroyed = false;

void android_main(struct android_app* app)
{
    switch (s_InitializationStatus) {
        case InitializationStatus::NotInitialized:
            UNITY_FATAL_ERROR("UnityPlayerForGameActivity java object should be initialized before android_main call.");
            break;
        case InitializationStatus::Failure:
            // Loop while showing AlertDialog with relevant information
            IdleLoop(app);
            break;
        case InitializationStatus::Success:
            if (!s_Destroyed)
            {
                MainLoop(app);
                Unity::UnityApplication::DestroyInstance();
                // This is required to continue destroy process on resume if it was not finished because the app was backgrounded
                s_Destroyed = true;
            }
            // This ensures onDestroy() is called on the Java side before process exit
            IdleLoop(app);
            Unity::UnityApplication::FinalDestroyInstance();
            s_Destroyed = false;
            break;
        default:
            UNITY_FATAL_ERROR("Invalid initialization status: %d", s_InitializationStatus);
            break;
    }

    // Note: Game Activity process doesn't quit after exiting this function, in Activity based setup we request process kill.
    //       Do the same here. Unlike exit, _exit will exit process immediately
    UNITY_LOG_INFO("Quitting process");
    _exit(0);
}

#endif
