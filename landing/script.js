(function () {
  "use strict";

  /* ---------- Mobile nav toggle ---------- */
  var toggle = document.getElementById("nav-toggle");
  var navLinks = document.getElementById("nav-links");

  if (toggle && navLinks) {
    toggle.addEventListener("click", function () {
      var open = navLinks.classList.toggle("open");
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
    });

    navLinks.addEventListener("click", function (event) {
      if (event.target.closest("a")) {
        navLinks.classList.remove("open");
        toggle.setAttribute("aria-expanded", "false");
      }
    });
  }

  /* ---------- Scrollspy ---------- */
  var sections = Array.prototype.slice
    .call(document.querySelectorAll("main section[id]"))
    .map(function (section) {
      return { id: section.id, el: section };
    });
  var navAnchors = Array.prototype.slice.call(
    document.querySelectorAll(".nav-links a[href^='#']")
  );

  function setActive(id) {
    navAnchors.forEach(function (anchor) {
      var active = anchor.getAttribute("href") === "#" + id;
      anchor.classList.toggle("active", active);
    });
  }

  var ticking = false;
  function onScroll() {
    if (ticking) return;
    ticking = true;
    requestAnimationFrame(function () {
      var scrollPos = window.scrollY + 120;
      var current = sections[0] ? sections[0].id : "";
      for (var i = 0; i < sections.length; i++) {
        if (sections[i].el.offsetTop <= scrollPos) {
          current = sections[i].id;
        }
      }
      setActive(current);
      ticking = false;
    });
  }

  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  /* ---------- Explainer animation (hero caption overlay) ---------- */
  var enLine = document.querySelector(".preview-overlay-line:not(.preview-overlay-line--tl)");
  var tlLine = document.querySelector(".preview-overlay-line.preview-overlay-line--tl");

  if (enLine && tlLine) {
    var prefersReduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    if (!prefersReduced) {
      var SEGMENTS = [
        {
          en: "Hi everyone, welcome to the meeting. Today we're going over the roadmap…",
          tl: "Kumusta sa lahat, maligayang pagdating sa pulong. Ngayon ay tatalakayin natin ang roadmap…"
        },
        {
          en: "First up, we shipped the new caption overlay last week…",
          tl: "Una sa lahat, inilunsad namin ang bagong caption overlay noong nakaraang linggo…"
        },
        {
          en: "It runs entirely on your machine — no audio ever leaves your PC…",
          tl: "Tumatakbo ito nang buo sa iyong makina — walang audio na lumalabas sa iyong PC…"
        }
      ];
      var SEGMENT_PAUSE_MS = 1600;
      var WORD_STEP_MS = 320;
      var TL_OFFSET_MS = 900;
      var FADE_MS = 160;

      var segmentIndex = 0;
      var timers = [];

      function clearTimers() {
        for (var i = 0; i < timers.length; i++) clearTimeout(timers[i]);
        timers = [];
      }

      function renderWords(target, words, count) {
        var parts = [];
        for (var i = 0; i < count && i < words.length; i++) {
          parts.push(words[i]);
        }
        target.textContent = parts.join(" ");
      }

      function fadeIn(target) {
        target.style.transition = "opacity " + FADE_MS + "ms ease";
        target.style.opacity = "1";
      }

      function fadeOut(target, done) {
        target.style.transition = "opacity " + FADE_MS + "ms ease";
        target.style.opacity = "0";
        timers.push(setTimeout(done, FADE_MS + 20));
      }

      function playSegment(idx) {
        var seg = SEGMENTS[idx];
        if (!seg) return;
        var enWords = seg.en.split(" ");
        var tlWords = seg.tl.split(" ");

        enLine.style.opacity = "0";
        tlLine.style.opacity = "0";
        enLine.textContent = "";
        tlLine.textContent = "";

        timers.push(setTimeout(function () {
          fadeIn(enLine);
          fadeIn(tlLine);

          for (var i = 1; i <= enWords.length; i++) {
            timers.push(setTimeout((function (n) {
              return function () { renderWords(enLine, enWords, n); };
            })(i), i * WORD_STEP_MS));
          }

          for (var j = 1; j <= tlWords.length; j++) {
            timers.push(setTimeout((function (n) {
              return function () { renderWords(tlLine, tlWords, n); };
            })(j), j * WORD_STEP_MS + TL_OFFSET_MS));
          }

          var advanceAt = Math.max(enWords.length, tlWords.length) * WORD_STEP_MS + TL_OFFSET_MS + SEGMENT_PAUSE_MS;
          timers.push(setTimeout(function () {
            var current = segmentIndex;
            fadeOut(enLine, function () {});
            fadeOut(tlLine, function () {
              if (current === segmentIndex) {
                segmentIndex = (segmentIndex + 1) % SEGMENTS.length;
                timers.push(setTimeout(function () { playSegment(segmentIndex); }, FADE_MS + 40));
              }
            });
          }, advanceAt));
        }, FADE_MS + 30));
      }

      var mock = document.querySelector(".product-mock");
      if (mock && "IntersectionObserver" in window) {
        var started = false;
        var io = new IntersectionObserver(function (entries) {
          for (var i = 0; i < entries.length; i++) {
            if (entries[i].isIntersecting && !started) {
              started = true;
              playSegment(0);
              io.disconnect();
            }
          }
        }, { threshold: 0.25 });
        io.observe(mock);
      } else {
        playSegment(0);
      }
    }
  }
})();
