/**
 * @file config.js
 * @description Global client-side configuration.
 * Override API_BASE_URL for production deployment.
 */

const CONFIG = {
  /** Base URL of the ViewOwl REST API. Empty string = same origin (production). */
  API_BASE_URL: "",

  /** localStorage key used to store the JWT Bearer token. */
  TOKEN_KEY: "viewowl_token",

  /** localStorage key used to cache the current user profile. */
  USER_KEY: "viewowl_user",
};
