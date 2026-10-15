package com.xuanniao.flyscreen.ui

import android.annotation.SuppressLint
import android.content.Context
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import kotlin.math.abs

/**
 * 单指相对移动 → 鼠标；双指纵向 → 滚轮。
 */
class TouchpadView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {

    var onMove: ((dx: Float, dy: Float) -> Unit)? = null
    var onWheel: ((delta: Int) -> Unit)? = null
    var onTap: (() -> Unit)? = null

    private var lastX = 0f
    private var lastY = 0f
    private var moved = false
    private var twoFingerY = 0f

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                lastX = event.x
                lastY = event.y
                moved = false
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                if (event.pointerCount >= 2) {
                    twoFingerY = (event.getY(0) + event.getY(1)) / 2f
                }
            }
            MotionEvent.ACTION_MOVE -> {
                if (event.pointerCount >= 2) {
                    val y = (event.getY(0) + event.getY(1)) / 2f
                    val dy = y - twoFingerY
                    if (abs(dy) > 8f) {
                        onWheel?.invoke((-dy * 4f).toInt().coerceIn(-480, 480))
                        twoFingerY = y
                        moved = true
                    }
                } else {
                    val dx = event.x - lastX
                    val dy = event.y - lastY
                    if (abs(dx) > 0.5f || abs(dy) > 0.5f) {
                        onMove?.invoke(dx, dy)
                        lastX = event.x
                        lastY = event.y
                        moved = true
                    }
                }
            }
            MotionEvent.ACTION_UP -> {
                if (!moved) onTap?.invoke()
            }
        }
        return true
    }
}
